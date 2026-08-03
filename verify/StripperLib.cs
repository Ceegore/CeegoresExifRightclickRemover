// Duplicate of StripperLib for the verifier project (the engine's Strip method is internal).
// This wraps the public StripPipeline.Strip with a "give me the bytes back" helper.

using System;
using ExifRemover.Engine;

namespace ExifRemover.Verifier;

public static class StripperLib
{
    public static byte[] Strip(byte[] input, StripProfile profile)
    {
        var tempInput = System.IO.Path.GetTempFileName();
        var tempOutput = System.IO.Path.GetTempFileName();
        try
        {
            System.IO.File.WriteAllBytes(tempInput, input);
            StripPipeline.Strip(tempInput, tempOutput, false, profile);
            return System.IO.File.ReadAllBytes(tempOutput);
        }
        finally
        {
            try { System.IO.File.Delete(tempInput); } catch { }
            try { System.IO.File.Delete(tempOutput); } catch { }
        }
    }
}
