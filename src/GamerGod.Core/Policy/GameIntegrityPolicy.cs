using System.Collections.Immutable;
using GamerGod.Core.Mutations;

namespace GamerGod.Core.Policy;

/// <summary>
/// Whether the user has opted this specific title into contact optimisations.
/// </summary>
public enum ContactPreference
{
    /// <summary>
    /// Never touch the game process. The default for every title, always.
    /// Ambient optimisations still apply in full.
    /// </summary>
    Never = 0,

    /// <summary>
    /// The user has explicitly opted this title in. Contact is still refused unless GamerGod
    /// is independently confident no kernel anti-cheat is involved — an opt-in is a
    /// permission, not an override.
    /// </summary>
    WhenProvablySafe = 1,
}

/// <summary>
/// Proof that <see cref="GameIntegrityPolicy"/> authorised contact with a specific title.
///
/// <para>
/// The constructor is private and the only factory is internal to this file, so a
/// <see cref="ContactGrant"/> cannot be forged elsewhere in the codebase. The ledger
/// requires one before applying any <see cref="MutationVisibility.Contact"/> mutation, which
/// turns Charter Article I from a rule people must remember into one the compiler helps
/// enforce.
/// </para>
/// </summary>
public sealed class ContactGrant
{
    private ContactGrant(string title, AntiCheatTier observedTier)
    {
        Title = title;
        ObservedTier = observedTier;
    }

    public string Title { get; }

    /// <summary>The tier observed at the moment the grant was issued.</summary>
    public AntiCheatTier ObservedTier { get; }

    internal static ContactGrant Issue(string title, AntiCheatTier observedTier)
    {
        if (observedTier is not (AntiCheatTier.None or AntiCheatTier.UserMode))
        {
            // Defence in depth. Reaching here means a caller inside Core tried to issue a
            // grant the policy would have refused, so fail loudly rather than silently
            // producing a grant that violates the Charter.
            throw new InvalidOperationException(
                $"Refusing to issue a contact grant for '{title}': observed anti-cheat tier " +
                $"is {observedTier}. Contact is only ever permitted for None or UserMode.");
        }

        return new ContactGrant(title, observedTier);
    }
}

/// <summary>
/// The set of mutations GamerGod is allowed to apply to a given title right now.
/// </summary>
public sealed record MutationPermit
{
    public required string Title { get; init; }

    public required AntiCheatAssessment Assessment { get; init; }

    /// <summary>Null when contact is refused, which is the common and default case.</summary>
    public ContactGrant? Contact { get; init; }

    public bool IsAmbientOnly => Contact is null;

    public bool Allows(IMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        return mutation.Visibility == MutationVisibility.Ambient || Contact is not null;
    }

    /// <summary>
    /// Filters a proposed mutation set down to what is permitted. Rejected mutations are
    /// dropped rather than causing a failure: an ambient-only session is a perfectly good
    /// session, and refusing to run at all because one lever is unavailable would punish
    /// the user for playing a protected game.
    /// </summary>
    public ImmutableArray<IMutation> Filter(IEnumerable<IMutation> proposed)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        return proposed.Where(Allows).ToImmutableArray();
    }

    public string Explain()
    {
        if (Contact is not null)
        {
            return $"Contact permitted for '{Title}'. {Assessment.Explain()}";
        }

        return Assessment.Tier switch
        {
            AntiCheatTier.Kernel =>
                $"Ambient only for '{Title}': kernel anti-cheat detected. GamerGod will not "
                + $"open a handle to this game. {Assessment.Explain()}",
            AntiCheatTier.Unknown =>
                $"Ambient only for '{Title}': no anti-cheat signature matched, which GamerGod "
                + "treats as protected rather than assuming it is safe.",
            _ =>
                $"Ambient only for '{Title}': contact has not been opted into for this title.",
        };
    }
}

/// <summary>
/// Decides what GamerGod may do to a title. The single choke point for Charter Article I.
/// </summary>
public static class GameIntegrityPolicy
{
    /// <summary>
    /// Evaluates a title. Contact is granted only when the user has explicitly opted that
    /// title in <em>and</em> the assessment independently shows no kernel anti-cheat.
    ///
    /// <para>
    /// Note what is deliberately absent: there is no force flag, no override, and no
    /// "advanced users" escape hatch. A setting that lets someone bypass this would, in
    /// practice, be the setting that gets them banned.
    /// </para>
    /// </summary>
    public static MutationPermit Evaluate(
        string title,
        AntiCheatAssessment assessment,
        ContactPreference preference = ContactPreference.Never)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(assessment);

        var grant = preference == ContactPreference.WhenProvablySafe && assessment.ContactIsConsidered
            ? ContactGrant.Issue(title, assessment.Tier)
            : null;

        return new MutationPermit
        {
            Title = title,
            Assessment = assessment,
            Contact = grant,
        };
    }

    /// <summary>Convenience overload that assesses the environment and evaluates in one step.</summary>
    public static MutationPermit Evaluate(
        string title,
        AntiCheatEnvironment environment,
        ContactPreference preference = ContactPreference.Never) =>
        Evaluate(title, AntiCheatDetector.Assess(environment), preference);
}
