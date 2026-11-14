using BalloonMesh.Server.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BalloonMesh.Server.Tests;

public sealed class AuthTests
{
    [Fact]
    public void IdentityPasswordHasherRoundTrips()
    {
        var user = new ApplicationUser { UserName = "test-user" };
        var hasher = new PasswordHasher<ApplicationUser>();
        var hash = hasher.HashPassword(user, "correct horse battery staple");

        Assert.Equal(PasswordVerificationResult.Success,
            hasher.VerifyHashedPassword(user, hash, "correct horse battery staple"));
        Assert.Equal(PasswordVerificationResult.Failed,
            hasher.VerifyHashedPassword(user, hash, "wrong password"));
    }

    [Fact]
    public async Task IdentitySupportsMultipleUsersAndRoles()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "Local",
                ["Authentication:Local:Enabled"] = "true",
                ["Authentication:Local:Users:0:Username"] = "admin",
                ["Authentication:Local:Users:0:Password"] = "admin-password",
                ["Authentication:Local:Users:0:Role"] = "Admin",
                ["Authentication:Local:Users:1:Username"] = "operator",
                ["Authentication:Local:Users:1:Password"] = "operator-password",
                ["Authentication:Local:Users:1:Role"] = "Operator"
            })
            .Build();
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging();
        services.AddIdentityCore<ApplicationUser>().AddRoles<IdentityRole>();
        services.AddSingleton<IPasswordHasher<ApplicationUser>, PasswordHasher<ApplicationUser>>();
        services.AddSingleton<IUserStore<ApplicationUser>, InMemoryUserStore>();
        using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<UserManager<ApplicationUser>>();

        var admin = new ApplicationUser { UserName = "admin", DisplayName = "Admin" };
        var operatorUser = new ApplicationUser { UserName = "operator", DisplayName = "Operator" };
        var adminCreate = await manager.CreateAsync(admin, "Admin-password1");
        var operatorCreate = await manager.CreateAsync(operatorUser, "Operator-password1");
        Assert.True(adminCreate.Succeeded,
            string.Join("; ", adminCreate.Errors.Select(error => error.Description)));
        Assert.True(operatorCreate.Succeeded,
            string.Join("; ", operatorCreate.Errors.Select(error => error.Description)));

        Assert.True(await manager.CheckPasswordAsync(admin, "Admin-password1"));
        Assert.False(await manager.CheckPasswordAsync(admin, "Operator-password1"));
        Assert.True((await manager.AddToRoleAsync(admin, AuthRoles.Admin)).Succeeded);
        Assert.True((await manager.AddToRoleAsync(operatorUser, AuthRoles.Operator)).Succeeded);
        Assert.Equal([AuthRoles.Admin.ToUpperInvariant()], await manager.GetRolesAsync(admin));
        Assert.Equal([AuthRoles.Operator.ToUpperInvariant()], await manager.GetRolesAsync(operatorUser));
    }

    [Fact]
    public void AuthenticationModeIsExclusive()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "Discord",
                ["Authentication:Oidc:Authority"] = "https://idp.example",
                ["Authentication:Oidc:ClientId"] = "client",
                ["Authentication:Discord:ClientId"] = "discord-client",
                ["Authentication:Discord:ClientSecret"] = "secret"
            })
            .Build();

        Assert.False(AuthSetup.OidcEnabled(configuration));
        Assert.True(AuthSetup.DiscordEnabled(configuration));
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("/api/state", "/api/state")]
    [InlineData("//evil.example", "/")]
    [InlineData("https://evil.example", "/")]
    [InlineData("\\\\evil", "/")]
    public void ReturnUrlMustBeLocal(string? input, string expected)
    {
        Assert.Equal(expected, AuthSetup.SafeReturnUrl(input));
    }

    private sealed class InMemoryUserStore : IUserPasswordStore<ApplicationUser>,
        IUserRoleStore<ApplicationUser>
    {
        private readonly Dictionary<string, ApplicationUser> _users = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _roles = new(StringComparer.Ordinal);

        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken _) {
            user.Id = Guid.NewGuid().ToString("N");
            _users[user.NormalizedUserName!] = user;
            return Task.FromResult(IdentityResult.Success!);
        }
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken _) =>
            Task.FromResult(IdentityResult.Success!);
        public Task<ApplicationUser?> FindByIdAsync(string id, CancellationToken _) =>
            Task.FromResult(_users.Values.SingleOrDefault(user => user.Id == id));
        public Task<ApplicationUser?> FindByNameAsync(string normalized, CancellationToken _) =>
            Task.FromResult(_users.TryGetValue(normalized, out var user) ? user : null);
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken _) => Task.FromResult(user.Id);
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken _) => Task.FromResult(user.UserName);
        public Task SetUserNameAsync(ApplicationUser user, string? value, CancellationToken _) { user.UserName = value; return Task.CompletedTask; }
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken _) => Task.FromResult(user.NormalizedUserName);
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? value, CancellationToken _) { user.NormalizedUserName = value; return Task.CompletedTask; }
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken _) => Task.FromResult(IdentityResult.Success!);
        public Task SetPasswordHashAsync(ApplicationUser user, string? hash, CancellationToken _) { user.PasswordHash = hash; return Task.CompletedTask; }
        public Task<string?> GetPasswordHashAsync(ApplicationUser user, CancellationToken _) => Task.FromResult(user.PasswordHash);
        public Task<bool> HasPasswordAsync(ApplicationUser user, CancellationToken _) => Task.FromResult(user.PasswordHash is not null);
        public Task AddToRoleAsync(ApplicationUser user, string role, CancellationToken _) { if (!_roles.TryGetValue(user.Id, out var roles)) _roles[user.Id] = roles = []; roles.Add(role); return Task.CompletedTask; }
        public Task RemoveFromRoleAsync(ApplicationUser user, string role, CancellationToken _) { if (_roles.TryGetValue(user.Id, out var roles)) roles.Remove(role); return Task.CompletedTask; }
        public Task<IList<string>> GetRolesAsync(ApplicationUser user, CancellationToken _) => Task.FromResult<IList<string>>(_roles.TryGetValue(user.Id, out var roles) ? roles.ToList() : []);
        public Task<bool> IsInRoleAsync(ApplicationUser user, string role, CancellationToken _) => Task.FromResult(_roles.TryGetValue(user.Id, out var roles) && roles.Contains(role));
        public Task<IList<ApplicationUser>> GetUsersInRoleAsync(string role, CancellationToken _) => Task.FromResult<IList<ApplicationUser>>(_users.Values.Where(user => _roles.TryGetValue(user.Id, out var roles) && roles.Contains(role)).ToList());
        public void Dispose() { }
    }
}
