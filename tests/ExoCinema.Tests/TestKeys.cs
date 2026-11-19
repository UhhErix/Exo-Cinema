using System.Security.Cryptography;
using Exo.Identity;

namespace ExoCinema.Tests;

/// <summary>A PC's two keys in memory, as Exo Launcher's identity tests keep them (its TestKeys.Store), for the shared
/// identity vectors (IdentityVectorsTests).</summary>
internal static class TestKeys
{
    internal sealed class Store : IDeviceKeyStore
    {
        private ECParameters? _identity, _encryption;

        public DeviceKeys? Open() => _identity is { } i && _encryption is { } e
            ? new(new EcdsaSigner(ECDsa.Create(i)), new EcdhDecrypter(ECDiffieHellman.Create(e)))
            : null;

        public DeviceKeys OpenOrCreate()
        {
            if (_identity is null || _encryption is null)
            {
                using var identity = ECDsa.Create(P256.Curve);
                using var encryption = ECDiffieHellman.Create(P256.Curve);
                (_identity, _encryption) = (identity.ExportParameters(true), encryption.ExportParameters(true));
            }
            return Open()!;
        }
    }
}
