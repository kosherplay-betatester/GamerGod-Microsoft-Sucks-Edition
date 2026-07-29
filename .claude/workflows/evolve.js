export const meta = {
  name: 'evolve',
  description: 'Master architect plans GodMode\'s next move, dispatches specialists in parallel, then reviews',
  whenToUse:
    'Run with args = a goal ("make onboarding great", "cut startup cost", "ship the overlay"), ' +
    'or with no args to let the architect choose what matters most. It plans, fans the plan out ' +
    'to the right specialists, reviews the result against the Charter, and reports what it cut.',
  phases: [
    { title: 'Plan', detail: 'master architect surveys the product and decides what to build' },
    { title: 'Execute', detail: 'specialists work the plan in parallel' },
    { title: 'Review', detail: 'Charter critic judges the result and finds what is missing' },
  ],
}

const GOAL =
  typeof args === 'string' && args.trim()
    ? args.trim()
    : 'Decide what matters most right now and plan it. Read the product, run the tests, and choose.'

const PLAN_SCHEMA = {
  type: 'object',
  additionalProperties: false,
  required: ['stateOfProduct', 'gap', 'tasks', 'cut'],
  properties: {
    stateOfProduct: { type: 'string' },
    gap: { type: 'string', description: 'The single most valuable thing a player cannot do today' },
    tasks: {
      type: 'array',
      maxItems: 8,
      items: {
        type: 'object',
        additionalProperties: false,
        required: ['agent', 'task', 'whyNow', 'doneWhen', 'independent'],
        properties: {
          agent: {
            type: 'string',
            enum: [
              'godmode-feature-architect',
              'godmode-feature-builder',
              'godmode-charter-critic',
              'godmode-ux-designer',
              'godmode-perf-engineer',
            ],
          },
          task: { type: 'string' },
          whyNow: { type: 'string' },
          doneWhen: { type: 'string', description: 'Observable and testable, never "implemented"' },
          effort: { type: 'string' },
          risk: { type: 'string' },
          independent: {
            type: 'boolean',
            description: 'true if this can run at the same time as the other tasks',
          },
        },
      },
    },
    cut: { type: 'array', items: { type: 'string' }, description: 'What to drop, defer or kill' },
    newSpecialistNeeded: { type: 'string' },
  },
}

const RESULT_SCHEMA = {
  type: 'object',
  additionalProperties: false,
  required: ['completed', 'summary'],
  properties: {
    completed: { type: 'boolean' },
    summary: { type: 'string' },
    filesChanged: { type: 'array', items: { type: 'string' } },
    testsPassing: { type: 'boolean' },
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
    defects: { type: 'array', items: { type: 'string' } },
    missingTests: { type: 'array', items: { type: 'string' } },
    checkedClean: { type: 'array', items: { type: 'string' } },
  },
}

phase('Plan')

const plan = await agent(
  `${GOAL}

Survey the product first: read the Charter, the product direction, the README and the source,
and run the test suite. Then produce a plan.

Assign every task to exactly one specialist and state an observable "done when". Mark tasks
independent only if they genuinely touch different files - two agents editing the same file in
parallel will conflict and waste the work.

You must fill in "cut". A plan that only adds is a wish list.`,
  { label: 'architect', phase: 'Plan', agentType: 'godmode-master-architect', schema: PLAN_SCHEMA }
)

if (!plan || !plan.tasks?.length) {
  log('Architect produced no plan.')
  return { outcome: 'no-plan', goal: GOAL }
}

log(`Gap identified: ${plan.gap}`)
log(`${plan.tasks.length} task(s). Cutting: ${plan.cut.join('; ') || 'nothing stated'}`)
if (plan.newSpecialistNeeded) {
  log(`Architect wants a new specialist: ${plan.newSpecialistNeeded}`)
}

phase('Execute')

const parallelTasks = plan.tasks.filter(t => t.independent)
const serialTasks = plan.tasks.filter(t => !t.independent)

log(`${parallelTasks.length} in parallel, ${serialTasks.length} in sequence.`)

function brief(t, index) {
  return `TASK ${index + 1} of ${plan.tasks.length}

${t.task}

Why this matters now: ${t.whyNow}
Done when: ${t.doneWhen}

Context - the gap this plan is closing:
${plan.gap}

Work only on this task. Do not expand scope into other tasks in the same plan; other agents
are working them. If you cannot complete it, say what blocked you rather than substituting
something easier.`
}

const parallelResults = await parallel(
  parallelTasks.map((t, i) => () =>
    agent(brief(t, i), {
      label: `${t.agent.replace('godmode-', '')}:${i + 1}`,
      phase: 'Execute',
      agentType: t.agent,
      schema: RESULT_SCHEMA,
    }).then(r => ({ task: t.task, agent: t.agent, ...r }))
  )
)

// Serial tasks run one at a time because they were marked as touching shared files.
const serialResults = []
for (let i = 0; i < serialTasks.length; i++) {
  const t = serialTasks[i]
  const r = await agent(brief(t, parallelTasks.length + i), {
    label: `${t.agent.replace('godmode-', '')}:seq${i + 1}`,
    phase: 'Execute',
    agentType: t.agent,
    schema: RESULT_SCHEMA,
  })

  serialResults.push({ task: t.task, agent: t.agent, ...(r || { completed: false, summary: 'no report' }) })

  if (r && !r.completed) {
    log(`Sequential task ${i + 1} did not complete; continuing with the rest.`)
  }
}

const done = [...parallelResults.filter(Boolean), ...serialResults]
const finished = done.filter(r => r.completed).length

log(`${finished}/${plan.tasks.length} task(s) completed.`)

phase('Review')

const review = await agent(
  `Review everything changed in the working tree against the Charter, and hunt for what is
missing.

This round was aiming at: ${plan.gap}

Work reported:
${done.map(r => `- [${r.agent}] ${r.task}\n    ${r.summary}`).join('\n')}

Run the full test suite yourself. Read test bodies, not just names. Be willing to say
do-not-ship.`,
  { label: 'critic', phase: 'Review', agentType: 'godmode-charter-critic', schema: REVIEW_SCHEMA }
)

return {
  goal: GOAL,
  stateOfProduct: plan.stateOfProduct,
  gap: plan.gap,
  cut: plan.cut,
  newSpecialistNeeded: plan.newSpecialistNeeded,
  tasksPlanned: plan.tasks.length,
  tasksCompleted: finished,
  work: done.map(r => ({
    agent: r.agent,
    task: r.task,
    completed: r.completed,
    testsPassing: r.testsPassing,
    filesChanged: r.filesChanged,
    blockers: r.blockers,
  })),
  verdict: review?.verdict ?? 'unknown',
  charterViolations: review?.charterViolations ?? [],
  defects: review?.defects ?? [],
  missingTests: review?.missingTests ?? [],
  verifiedClean: review?.checkedClean ?? [],
}
