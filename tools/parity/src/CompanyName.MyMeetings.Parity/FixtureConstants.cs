namespace CompanyName.MyMeetings.Parity;

/// <summary>Determinism anchors shared by capture and verify.</summary>
public static class FixtureConstants
{
    /// <summary>
    /// The frozen wall clock reused from the payments integration tests
    /// (<c>SystemClock.Set(new DateTime(2020,6,15))</c>).
    /// </summary>
    public const string FrozenClockUtc = "2020-06-15T00:00:00.0000000";

    // Sign-in identities used to authenticate the API golden dataset. Both are created by the SUT
    // ParityFixture harness with real password hashes and correct roles: a regular user (registration
    // confirmation assigns UserRole.Member) and the admin (UsersFactory.GivenAdmin → Administrator).
    public const string MemberLogin = "adamSmith@mail.com";
    public const string MemberPassword = "adamSmithPass";

    public const string AdminLogin = "testAdmin@mail.com";
    public const string AdminPassword = "testAdminPass";

    // OAuth2 resource-owner-password client (IdentityServerConfig.cs). The client's allowed scopes are
    // { "all", openid, profile }; "all" maps to the myMeetingsAPI ApiResource, so the issued token's
    // audience is accepted by the API. (The brief's "myMeetingsAPI" scope is the resource name, not a
    // grantable scope — requesting it yields invalid_scope; "all" is the correct grantable scope.)
    public const string OAuthClientId = "ro.client";
    public const string OAuthClientSecret = "secret";
    public const string OAuthScope = "all openid profile";

    // Stable role labels recorded in the golden dataset.
    public const string MemberRole = "Member";
    public const string AdminRole = "Administrator";
}
