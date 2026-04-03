using System;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace BTCPayServer.Plugins.Template.Services;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MyPluginDbContext>
{
    public MyPluginDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<MyPluginDbContext>();
        builder.UseNpgsql("User ID=postgres;Host=127.0.0.1;Port=39372;Database=designtimebtcpay");
        return new MyPluginDbContext(builder.Options, true);
    }
}

public class MyPluginDbContextFactory : BaseDbContextFactory<MyPluginDbContext>
{
    public MyPluginDbContextFactory(IOptions<DatabaseOptions> options) : base(options, "BTCPayServer.Plugins.Template")
    {
    }

    public override MyPluginDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder> npgsqlOptionsAction = null)
    {
        var builder = new DbContextOptionsBuilder<MyPluginDbContext>();
        ConfigureBuilder(builder, npgsqlOptionsAction);
        return new MyPluginDbContext(builder.Options);
    }
}
