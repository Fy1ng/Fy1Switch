using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Fy1Switch.Relay;

public sealed class RelayIdentityDbContext(DbContextOptions<RelayIdentityDbContext> options)
    : IdentityDbContext<IdentityUser>(options);
