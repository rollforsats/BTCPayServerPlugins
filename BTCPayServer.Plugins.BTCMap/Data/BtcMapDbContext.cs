using BTCPayServer.Plugins.BTCMap.Data;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.BTCMap;

public class BtcMapDbContext : DbContext
{
    private readonly bool _designTime;

    public BtcMapDbContext(DbContextOptions<BtcMapDbContext> options, bool designTime = false)
        : base(options)
    {
        _designTime = designTime;
    }

    public DbSet<BtcMapListing> Listings { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("BTCPayServer.Plugins.BTCMap");

        modelBuilder.Entity<BtcMapListing>(b =>
        {
            b.HasIndex(e => e.StoreId).IsUnique();
            b.HasIndex(e => new { e.Status, e.LastVerifiedAt });
        });
    }
}
