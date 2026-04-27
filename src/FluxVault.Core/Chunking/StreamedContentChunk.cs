namespace FluxVault.Core.Chunking;

public sealed record StreamedContentChunk(long Offset, byte[] Payload);
