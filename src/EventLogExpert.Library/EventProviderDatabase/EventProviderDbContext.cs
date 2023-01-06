﻿// // Copyright (c) Microsoft Corporation.
// // Licensed under the MIT License.

using EventLogExpert.Library.Models;
using EventLogExpert.Library.Providers;
using Microsoft.EntityFrameworkCore;

namespace EventLogExpert.Library.EventProviderDatabase;

public class EventProviderDbContext : DbContext
{
    private readonly bool _readOnly;

    public EventProviderDbContext(string path, bool readOnly)
    {
        Name = System.IO.Path.GetFileNameWithoutExtension(path);
        Path = path;
        _readOnly = readOnly;

        Database.EnsureCreated();
    }

    public string Name { get; }

    public string Path { get; }

    public DbSet<ProviderDetails> ProviderDetails { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseSqlite($"Data Source={Path};Mode={(_readOnly ? "ReadOnly" : "ReadWriteCreate")}");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProviderDetails>()
            .HasKey(e => e.ProviderName);

        modelBuilder.Entity<ProviderDetails>()
            .Property(e => e.Messages)
            .HasConversion<JsonValueConverter<List<MessageModel>>>();

        modelBuilder.Entity<ProviderDetails>()
            .Property(e => e.Events)
            .HasConversion<JsonValueConverter<List<EventModel>>>();

        modelBuilder.Entity<ProviderDetails>()
            .Property(e => e.Keywords)
            .HasConversion<JsonValueConverter<List<ProviderDetails.ValueName>>>();

        modelBuilder.Entity<ProviderDetails>()
            .Property(e => e.Opcodes)
            .HasConversion<JsonValueConverter<List<ProviderDetails.ValueName>>>();

        modelBuilder.Entity<ProviderDetails>()
            .Property(e => e.Tasks)
            .HasConversion<JsonValueConverter<List<ProviderDetails.ValueName>>>();
    }
}
