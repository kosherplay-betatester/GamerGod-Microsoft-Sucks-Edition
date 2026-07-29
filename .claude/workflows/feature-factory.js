export const meta = {
  name: 'feature-factory',
  description: 'Design, build, review and harden a GodMode feature end to end',
  whenToUse:
    'Run with args = the feature description, e.g. "EcoQoS demotion of background processes". ' +
    'Chains architect -> builder -> critic -> fixer, and loops until the critic says SHIP or ' +
    'the round budget is spent. Declines anything that violates the Charter.',
  phases: [
    { title: 'Design', detail: 'architect turns the request into a buildable spec' },
    { title: 'Build', detail: 'builder implements it test-first' },
    { title: 'Review', detail: 'critic judges it against the Charter and hunts for gaps' },
    { title: 'Harden', detail: 'fix what the critic found, then re-review' },
  ],
}

const REQUEST =
  typeof args === 'string' && args.trim()
    ? args.trim()
    : 'Choose the highest-value unbuilt feature from docs/PRODUCT-DIRECTION.md and build it.'

const MAX_ROUNDS = 3

const SPEC_SCHEMA = {
  type: 'object',
  additionalProperties: false,
  required: ['verdict', 'spec'],
  properties: {
    verdict: { type: 'string', enum: ['build', 'build-with-changes', 'decline'] },
    declineReason: { type: 'string' },
    classification: { type: 'string', enum: ['Ambient', 'Contact'] },
    spec: { type: 'string', description: 'The full specification, in the architect output format' },
    openQuestions: { type: 'array', items: { type: 'string' } },
  },
}

const BUILD_SCHEMA = {
  type: 'object',
  additionalProperties: false,
  required: ['implemented', 'testsPassing', 'summary'],
  properties: {
    implemented: { type: 'boolean' },
    testsPassing: { type: 'boolean' },
    totalTests: { type: 'integer' },
    filesChanged: { type: 'array', items: { type: 'string' } },
    summary: { type: 'string' },
    blockers: { type: 'array', items: { type: 'string' } },
  },
}

const REVIEW_SCHEMA = {
  type: 'object',
  additionalProperties: false,
  required: ['verdict', 'defects', 'missingTests'],
  properties: {
    verdict: { type: 'string', enum: ['ship', 'fix-first', 'do-not-ship'] },
    charterViolations: { type: 'array', items: { type: 'string' } },
    defects: {
      type: 'array',
      items: {
        type: 'object',
        additionalProperties: false,
        required: ['severity', 'location', 'problem'],
        properties: {
          severity: { type: 'string', enum: ['critical', 'high', 'medium', 'low'] },
          location: { type: 'string' },
          problem: { type: 'string' },
        },
      },
    },
    missingTests: { type: 'array', items: { type: 'string' } },
    checkedClean: { type: 'array', items: { type: 'string' } },
  },
}

phase('Design')

const design = await agent(
  `Design this GodMode feature into a buildable specification.

REQUEST: ${REQUEST}

Answer all five questions your instructions require. If the request violates Charter
Articles I to VI, set verdict to "decline" and say which article and why - declining is a
correct and valuable outcome, not a failure.`,
  { label: 'architect', phase: 'Design', agentType: 'godmode-feature-architect', schema: SPEC_SCHEMA }
)

if (!design || design.verdict === 'decline') {
  log(`Declined: ${design?.declineReason ?? 'architect produced no spec'}`)
  return { outcome: 'declined', reason: design?.declineReason, request: REQUEST }
}

log(`Spec ready (${design.classification}). ${design.openQuestions?.length ?? 0} open question(s).`)

phase('Build')

const build = await agent(
  `Implement this specification test-first. Write the failing tests, watch them fail, then
write the minimum code to pass. Run the full suite before reporting.

${design.spec}`,
  { label: 'builder', phase: 'Build', agentType: 'godmode-feature-builder', schema: BUILD_SCHEMA }
)

if (!build || !build.implemented) {
  log(`Build did not complete: ${build?.blockers?.join('; ') ?? 'no report'}`)
  return { outcome: 'build-failed', spec: design.spec, blockers: build?.blockers }
}

log(`Built. ${build.testsPassing ? 'Suite green' : 'SUITE RED'} (${build.totalTests ?? '?'} tests).`)

// Review, fix, re-review until the critic is satisfied or the budget runs out. Each round
// starts from what the previous one actually changed, so effort concentrates where problems
// keep appearing rather than being spread evenly.
let review = null
const history = []

for (let round = 1; round <= MAX_ROUNDS; round++) {
  phase(round === 1 ? 'Review' : 'Harden')

  review = await agent(
    `Review the current state of the working tree against the Charter, and hunt for what is
missing. This is round ${round} of at most ${MAX_ROUNDS}.

The feature just built was:
${design.spec}

${history.length ? `Previously reported and supposedly fixed:\n${history.join('\n')}` : ''}

Run the test suite yourself. Read test bodies, not just names.`,
    { label: `critic:r${round}`, phase: round === 1 ? 'Review' : 'Harden', agentType: 'godmode-charter-critic', schema: REVIEW_SCHEMA }
  )

  if (!review) {
    log(`Round ${round}: critic produced no report.`)
    break
  }

  log(`Round ${round}: ${review.verdict}, ${review.defects.length} defect(s), ${review.missingTests.length} missing test(s).`)

  if (review.verdict === 'ship') {
    break
  }

  if (round === MAX_ROUNDS) {
    log('Round budget spent. Remaining defects are reported unfixed rather than hidden.')
    break
  }

  const work = [
    ...review.defects.map(d => `[${d.severity}] ${d.location}: ${d.problem}`),
    ...review.missingTests.map(t => `Missing test: ${t}`),
  ]

  history.push(...work)

  const fix = await agent(
    `A Charter review found the following in the current working tree. Fix every item, adding
a test for each one so it cannot regress. Do not weaken or delete a test to make it pass, and
do not add a flag that bypasses a safety check.

${work.map((w, i) => `${i + 1}. ${w}`).join('\n')}

Run the full suite and report honestly, including anything you could not fix.`,
    { label: `fixer:r${round}`, phase: 'Harden', agentType: 'godmode-feature-builder', schema: BUILD_SCHEMA }
  )

  if (!fix?.testsPassing) {
    log(`Round ${round} fixes left the suite red. Stopping rather than compounding.`)
    break
  }
}

return {
  outcome: review?.verdict ?? 'unknown',
  request: REQUEST,
  classification: design.classification,
  filesChanged: build.filesChanged,
  totalTests: build.totalTests,
  remainingDefects: review?.defects ?? [],
  remainingMissingTests: review?.missingTests ?? [],
  charterViolations: review?.charterViolations ?? [],
  verifiedClean: review?.checkedClean ?? [],
}
