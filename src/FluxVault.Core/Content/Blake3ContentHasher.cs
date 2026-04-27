using Blake3;

namespace FluxVault.Core.Content;

public sealed class Blake3ContentHasher
{
    public string Hash(ReadOnlySpan<byte> content)
    {
        return Hasher.Hash(content).ToString();
    }
}
