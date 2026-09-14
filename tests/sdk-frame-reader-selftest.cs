using System;
using System.IO;
using ClaudeCodeWorkbench;

internal static class FrameReaderTest
{
    private static int checks;
    private static void Check(bool value) { if (!value) throw new Exception("Frame reader assertion " + checks); checks++; }
    private sealed class EndlessReader : TextReader
    {
        internal int Reads;
        public override int Read() { Reads++; return 'x'; }
    }
    public static void Main()
    {
        using (var reader = new StringReader("\n中文\r\nabc\rZ"))
        {
            Check(SdkFrameReader.ReadLine(reader, 3) == "");
            Check(SdkFrameReader.ReadLine(reader, 3) == "中文");
            Check(SdkFrameReader.ReadLine(reader, 3) == "abc");
            Check(SdkFrameReader.ReadLine(reader, 3) == "Z");
            Check(SdkFrameReader.ReadLine(reader, 3) == null);
        }
        Check(SdkFrameReader.ReadLine(new StringReader("abc"), 3) == "abc");
        var endless = new EndlessReader();
        try { SdkFrameReader.ReadLine(endless, 1024); throw new Exception("Unbounded read accepted"); }
        catch (InvalidDataException) { Check(endless.Reads == 1025); }
        Console.WriteLine("SDK frame reader PASS: " + checks);
    }
}
