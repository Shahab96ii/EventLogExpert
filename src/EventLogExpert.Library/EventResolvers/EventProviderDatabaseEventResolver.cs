﻿// // Copyright (c) Microsoft Corporation.
// // Licensed under the MIT License.

using EventLogExpert.Library.EventProviderDatabase;
using EventLogExpert.Library.Helpers;
using EventLogExpert.Library.Models;
using EventLogExpert.Library.Providers;
using System.Collections.Immutable;
using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;

namespace EventLogExpert.Library.EventResolvers;

public interface IDatabaseEventResolver
{
    public ImmutableArray<string> ActiveDatabases { get; }

    public void SetActiveDatabases(IEnumerable<string> databasePaths);
}

public class EventProviderDatabaseEventResolver : EventResolverBase, IDatabaseEventResolver, IEventResolver, IDisposable
{
    public string Status { get; private set; } = string.Empty;

    public event EventHandler<string>? StatusChanged;

    private List<EventProviderDbContext> dbContexts = new();

    private volatile Dictionary<string, ProviderDetails?> _providerDetails = new();

    private volatile bool _ready = false;

    private bool disposedValue;

    public EventProviderDatabaseEventResolver() : this(ImmutableArray<string>.Empty, s => { }) { }

    public EventProviderDatabaseEventResolver(Action<string> tracer) : this(ImmutableArray<string>.Empty, tracer) { }

    public EventProviderDatabaseEventResolver(IEnumerable<string> activeDatabases) : this(activeDatabases, s => { }) { }

    public EventProviderDatabaseEventResolver(IEnumerable<string> activeDatabases, Action<string> tracer) : base(tracer)
    {
        if (activeDatabases != null && activeDatabases.Any())
        {
            ActiveDatabases = activeDatabases.ToImmutableArray();
        }
        else
        {
            ActiveDatabases = ImmutableArray<string>.Empty;
        }

        LoadDatabases();
    }

    /// <summary>
    /// Loads the databases. If ActiveDatabases is populated, any databases
    /// not named therein are skipped.
    /// </summary>
    private async void LoadDatabases()
    {
        _providerDetails = new();

        foreach (var context in dbContexts)
        {
            context.Dispose();
        }

        var databasesToLoad = ActiveDatabases.Where(db => File.Exists(db));
        databasesToLoad = SortDatabases(databasesToLoad);

        var contexts = await Task.Run(() =>
        {
            var contexts = new List<EventProviderDbContext>();
            foreach (var file in databasesToLoad)
            {
                var c = new EventProviderDbContext(file, readOnly: false, _tracer);
                var (needsv2, needsv3) = c.IsUpgradeNeeded();
                if (needsv2 || needsv3)
                {
                    UpdateStatus($"Upgrading database {c.Name}. Please wait...");
                    c.PerformUpgradeIfNeeded();
                }

                c.ChangeTracker.QueryTrackingBehavior = Microsoft.EntityFrameworkCore.QueryTrackingBehavior.NoTracking;
                contexts.Add(c);
            }

            UpdateStatus(string.Empty);

            return contexts;
        });

        dbContexts = contexts;

        _ready = true;
    }

    private void UpdateStatus(string message)
    {
        Status = message;
        StatusChanged?.Invoke(this, message);
    }

    /// <summary>
    /// If the database file name ends in a year or a number, such as Exchange 2019 or
    /// Windows 2016, we want to sort the database files by descending version, but by
    /// ascending product name. This generally means that databases named for products
    /// like Exchange will be checked for matching providers first, and Windows will
    /// be checked last, with newer versions being checked before older versions.
    /// </summary>
    /// <param name="databasePaths"></param>
    /// <returns></returns>
    private IEnumerable<string> SortDatabases(IEnumerable<string> databasePaths)
    {
        if (!databasePaths.Any())
        {
            return Array.Empty<string>();
        }

        var r = new Regex("^(.+) (\\S+)$");

        return databasePaths
            .Select(path =>
            {
                var name = Path.GetFileName(path);
                var directory = Path.GetDirectoryName(path);
                var m = r.Match(name);
                if (m.Success)
                {
                    return new
                    {
                        Directory = directory,
                        FirstPart = m.Groups[1].Value + " ",
                        SecondPart = m.Groups[2].Value
                    };
                }
                else
                {
                    return new
                    {
                        Directory = directory,
                        FirstPart = name,
                        SecondPart = ""
                    };
                }
            })
            .OrderBy(n => n.FirstPart)
            .ThenByDescending(n => n.SecondPart)
            .Select(n => Path.Join(n.Directory, n.FirstPart + n.SecondPart))
            .ToList();
    }

    /// <summary>
    /// Changes the active databases. Note that existing database
    /// connections are closed and the provider cache is cleared.
    /// </summary>
    /// <param name="databaseNames">
    /// If this value is empty, all available databases are loaded.
    /// Otherwise, only the databases specified in this value are loaded.
    /// Also, the order of this value determines the order in which
    /// we search the databases when attempting to find a matching
    /// provider.
    /// </param>
    public void SetActiveDatabases(IEnumerable<string> databaseNames)
    {
        _ready = false;
        ActiveDatabases = databaseNames.ToImmutableArray();
        LoadDatabases();
    }

    public DisplayEventModel Resolve(EventRecord eventRecord, string OwningLogName)
    {
        while (!_ready)
        {
            Thread.Sleep(100);
        }

        DisplayEventModel lastResult = null;

        // The Properties getter is expensive, so we only call the getter once,
        // and we pass this value separately from the eventRecord so it can be reused.
        var eventProperties = eventRecord.Properties;

        if (_providerDetails.ContainsKey(eventRecord.ProviderName))
        {
            _providerDetails.TryGetValue(eventRecord.ProviderName, out ProviderDetails? providerDetails);
            if (providerDetails != null)
            {
                lastResult = ResolveFromProviderDetails(eventRecord, eventProperties, providerDetails, OwningLogName);
            }
        }
        else
        {
            foreach (var dbContext in dbContexts)
            {
                var providerDetails = dbContext.ProviderDetails.FirstOrDefault(p => p.ProviderName == eventRecord.ProviderName);
                if (providerDetails != null)
                {
                    lastResult = ResolveFromProviderDetails(eventRecord, eventProperties, providerDetails, OwningLogName);

                    if (lastResult?.Description != null)
                    {
                        _tracer($"Resolved {eventRecord.ProviderName} provider from database {dbContext.Name}.");
                        _providerDetails.Add(eventRecord.ProviderName, providerDetails);
                        return lastResult;
                    }
                }
            }

            if (!_providerDetails.ContainsKey(eventRecord.ProviderName))
            {
                _providerDetails.Add(eventRecord.ProviderName, new ProviderDetails { ProviderName = eventRecord.ProviderName });
            }
        }

        if (lastResult == null)
        {
            return new DisplayEventModel(
                eventRecord.RecordId,
                eventRecord.TimeCreated!.Value.ToUniversalTime(),
                eventRecord.Id,
                eventRecord.MachineName,
                (SeverityLevel?)eventRecord.Level,
                eventRecord.ProviderName,
                "",
                "Description not found. No provider available.",
                eventProperties,
                eventRecord.Qualifiers,
                eventRecord.Keywords,
                eventRecord.LogName,
                null,
                OwningLogName);
        }

        if (lastResult.Description == null)
        {
            lastResult = lastResult with { Description = "" };
        }

        return lastResult;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                foreach (var context in dbContexts)
                {
                    context.Dispose();
                }
            }

            _providerDetails = null;

            disposedValue = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    public ImmutableArray<string> ActiveDatabases { get; private set; } = ImmutableArray<string>.Empty;
}
