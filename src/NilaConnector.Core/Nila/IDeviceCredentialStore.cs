namespace NilaConnector.Core.Nila;

public interface IDeviceCredentialStore
{
    event EventHandler? Changed;

    DeviceCredential? Load();

    void Save(DeviceCredential credential);

    void Clear();
}
