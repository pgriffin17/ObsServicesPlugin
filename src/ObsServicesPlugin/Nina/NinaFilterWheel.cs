using NINA.Core.Model.Equipment;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using ObsServices.Core.Devices;

namespace ObsServicesPlugin.Nina;

/// <summary>The N.I.N.A. filter wheel; filter names come from the active profile's filter list.</summary>
public sealed class NinaFilterWheel : IFilterWheelDevice {
    private readonly IFilterWheelMediator wheel;
    private readonly IProfileService profiles;

    public NinaFilterWheel(IFilterWheelMediator wheel, IProfileService profiles) {
        this.wheel = wheel;
        this.profiles = profiles;
    }

    public bool Connected => wheel.GetInfo()?.Connected == true;

    public IReadOnlyList<string> Filters => ProfileFilters().Select(f => f.Name).ToList();

    public string? CurrentFilter => wheel.GetInfo()?.SelectedFilter?.Name;

    public async Task ChangeFilterAsync(string filter, CancellationToken cancellationToken) {
        var target = ProfileFilters().FirstOrDefault(f => string.Equals(f.Name, filter, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"filter `{filter}` is not in the profile's filter list");
        var result = await wheel.ChangeFilter(target, cancellationToken);

        if (result == null) {
            throw new InvalidOperationException($"N.I.N.A. could not change to filter `{filter}`");
        }
    }

    private IEnumerable<FilterInfo> ProfileFilters() => profiles.ActiveProfile?.FilterWheelSettings?.FilterWheelFilters ?? Enumerable.Empty<FilterInfo>();
}
