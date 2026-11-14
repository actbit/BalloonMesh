using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace BalloonMesh.Server.Auth;

public sealed class ApplicationUser : IdentityUser
{
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class BalloonMeshIdentityDbContext
    : IdentityDbContext<ApplicationUser>
{
    public BalloonMeshIdentityDbContext(DbContextOptions<BalloonMeshIdentityDbContext> options)
        : base(options)
    {
    }
}

public static class IdentityBootstrapper
{
    public static async Task SeedAsync(IServiceProvider services,
        IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        if (!AuthSetup.LocalEnabled(configuration))
        {
            return;
        }

        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();

        foreach (var role in new[] { AuthRoles.Viewer, AuthRoles.Operator, AuthRoles.Admin })
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                var result = await roleManager.CreateAsync(new IdentityRole(role));
                EnsureSuccess(result, $"create role '{role}'");
            }
        }

        foreach (var section in configuration.GetSection("Authentication:Local:Users")
                     .GetChildren())
        {
            var username = section["Username"]?.Trim();
            if (string.IsNullOrWhiteSpace(username))
            {
                throw new InvalidOperationException(
                    "Authentication:Local:Users entries require Username.");
            }

            var role = section["Role"]?.Trim();
            if (string.IsNullOrWhiteSpace(role))
            {
                role = AuthRoles.Viewer;
            }
            if (role is not AuthRoles.Viewer and not AuthRoles.Operator and not AuthRoles.Admin)
            {
                throw new InvalidOperationException(
                    $"Local user '{username}' has unsupported role '{role}'.");
            }

            var displayName = section["DisplayName"]?.Trim();
            var user = await userManager.FindByNameAsync(username);
            if (user is null)
            {
                user = new ApplicationUser
                {
                    UserName = username,
                    DisplayName = string.IsNullOrWhiteSpace(displayName) ? username : displayName
                };

                var passwordEnvironmentVariable = section["PasswordEnvironmentVariable"]?.Trim();
                if (string.IsNullOrWhiteSpace(passwordEnvironmentVariable))
                {
                    throw new InvalidOperationException(
                        $"Local user '{username}' requires PasswordEnvironmentVariable.");
                }
                var plainPassword = Environment.GetEnvironmentVariable(passwordEnvironmentVariable);
                if (string.IsNullOrEmpty(plainPassword))
                {
                    throw new InvalidOperationException(
                        $"Environment variable '{passwordEnvironmentVariable}' for local user '{username}' is not set.");
                }

                EnsureSuccess(await userManager.CreateAsync(user, plainPassword),
                    $"create local user '{username}'");
            }
            else if (!string.IsNullOrWhiteSpace(displayName) &&
                     !string.Equals(user.DisplayName, displayName, StringComparison.Ordinal))
            {
                user.DisplayName = displayName;
                EnsureSuccess(await userManager.UpdateAsync(user),
                    $"update local user '{username}'");
            }

            var currentRoles = await userManager.GetRolesAsync(user);
            var rolesToRemove = currentRoles
                .Where(existing => !string.Equals(existing, role, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (rolesToRemove.Length > 0)
            {
                EnsureSuccess(await userManager.RemoveFromRolesAsync(user, rolesToRemove),
                    $"remove old roles from local user '{username}'");
            }
            if (!currentRoles.Contains(role, StringComparer.OrdinalIgnoreCase))
            {
                EnsureSuccess(await userManager.AddToRoleAsync(user, role),
                    $"assign role '{role}' to local user '{username}'");
            }
        }
    }

    private static void EnsureSuccess(IdentityResult result, string operation)
    {
        if (result.Succeeded)
        {
            return;
        }

        var details = string.Join("; ", result.Errors.Select(error => error.Description));
        throw new InvalidOperationException($"Unable to {operation}: {details}");
    }
}
