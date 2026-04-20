using System;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace BTCPayServer.Plugins.BTCMap.Services;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<BtcMapDbContext>
{
    public BtcMapDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<BtcMapDbContext>();
        builder.UseNpgsql("User ID=postgres;Host=127.0.0.1;Port=39372;Database=designtimebtcpay");
        return new BtcMapDbContext(builder.Options, true);
    }
}

public class BtcMapDbContextFactory : BaseDbContextFactory<BtcMapDbContext>
{
    public BtcMapDbContextFactory(IOptions<DatabaseOptions> options)
        : base(options, "BTCPayServer.Plugins.BTCMap")
    {
    }

    public override BtcMapDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder> npgsqlOptionsAction = null)
    {
        var builder = new DbContextOptionsBuilder<BtcMapDbContext>();
        ConfigureBuilder(builder, npgsqlOptionsAction);
        return new BtcMapDbContext(builder.Options);
    }
}
