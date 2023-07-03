﻿// // Copyright (c) Microsoft Corporation.
// // Licensed under the MIT License.

using EventLogExpert.Eventing.Models;

namespace EventLogExpert.UI.Models;

public record FilterModel
{
    private FilterType _filterType;

    public Guid Id { get; } = Guid.NewGuid();

    public List<Func<DisplayEventModel, bool>> Comparison { get; set; } = new();

    public string? ComparisonString { get; set; }

    public bool IsEditing { get; set; } = true;

    public bool IsEnabled { get; set; } = true;

    public FilterType FilterType
    {
        get => _filterType;
        set
        {
            _filterType = value;
            FilterValue = null;
            FilterValues.Clear();
        }
    }

    public FilterComparison FilterComparison { get; set; }

    public string? FilterValue { get; set; }

    public List<string> FilterValues { get; set; } = new();

    public List<SubFilterModel> SubFilters { get; set; } = new();
}
