namespace BentaGo.Core.Domain.Identity;

/// <summary>
/// Capability predicates (BG-005a decision, docs/01:26-34): Admin = all;
/// Supervisor = no user management, configuration, or high-risk adjustments;
/// Peddler = operational data only. Pure functions, so use cases gate on the
/// actor's role directly. Peddler-scoped navigation is a later slice — the
/// checks exist now so no use case can ship without them.
/// </summary>
public static class RoleExtensions
{
    /// <summary>Admin only — creating, deactivating, and re-roleing users.</summary>
    public static bool CanManageUsers(this Role role) => role == Role.Admin;

    /// <summary>Admin only — system configuration changes.</summary>
    public static bool CanManageConfiguration(this Role role) => role == Role.Admin;

    /// <summary>Admin only — high-risk adjustments and overrides.</summary>
    public static bool CanMakeHighRiskAdjustments(this Role role) => role == Role.Admin;
}