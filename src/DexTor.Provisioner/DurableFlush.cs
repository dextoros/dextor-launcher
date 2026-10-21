namespace DexTor.Provisioner;

/// <summary>Flushes to the device, tolerating devices that cannot be asked to.</summary>
/// <remarks>
/// Flushing to the device sends the drive a cache-flush command. Some USB sticks do not
/// implement it, and Windows then answers ERROR_INVALID_FUNCTION - surfaced as a bare
/// "Incorrect function." that stopped USB creation on such a stick. Nothing is lost by
/// going on: the raw image is written with write-through, and every payload is read back
/// and hash-checked before the stick is marked ready, which is what actually proves the
/// data arrived. Any other flush failure is still a failure.
/// </remarks>
internal static class DurableFlush
{
    internal const int ErrorInvalidFunctionHResult = unchecked((int)0x80070001);

    internal static void Flush(FileStream stream)
    {
        try
        {
            stream.Flush(flushToDisk: true);
        }
        catch (IOException exception) when (exception.HResult == ErrorInvalidFunctionHResult)
        {
            stream.Flush(flushToDisk: false);
        }
    }
}
