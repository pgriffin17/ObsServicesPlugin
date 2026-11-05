using NINA.Equipment.Interfaces.Mediator;
using ObsServices.Core.Devices;

namespace ObsServicesPlugin.Nina;

public sealed class NinaFocuser : IFocuserDevice {
    private readonly IFocuserMediator focuser;

    public NinaFocuser(IFocuserMediator focuser) {
        this.focuser = focuser;
    }

    public bool Connected => focuser.GetInfo()?.Connected == true;

    public int? Position => focuser.GetInfo() is { Connected: true } info ? info.Position : null;

    public async Task MoveAsync(int position, CancellationToken cancellationToken) {
        await focuser.MoveFocuser(position, cancellationToken);
    }
}
