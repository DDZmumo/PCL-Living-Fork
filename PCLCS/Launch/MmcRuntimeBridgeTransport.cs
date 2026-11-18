using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
// javac 17 --release 8 -encoding UTF-8 -g:none; shipped JavaWrapper.jar is unchanged.
public static class MmcRuntimeBridgeTransport {
    public const string EntryPoint = "pcllf.mmc.runtime.ArgvBridge";
    public const int MaximumFrameBytes = 1048576, MaximumStringBytes = 262144, MaximumArgumentCount = 4096;
    public const int MaximumCompressedEnvelopeBytes = MaximumFrameBytes + 4096;
    public const string ClassSha256 = "dbcde6bc07b3981dc59bda69b21adad092bc335901f1fa0a5cd6953e01c1d94a";
    private const string ClassPayload = "yv66vgAAADQA8gcAAgEAHHBjbGxmL21tYy9ydW50aW1lL0FyZ3ZCcmlkZ2UKAAQABQcABgwABwAIAQAQamF2YS9sYW5nL09iamVjdAEABjxpbml0PgEAAygpVgoACgALBwAMDAANAA4BABdqYXZhL2lvL0RhdGFJbnB1dFN0cmVhbQEAB3JlYWRJbnQBAAMoKUkDAAQAAAoACgARDAASAA4BAAlhdmFpbGFibGUHABQBABNqYXZhL2lvL0lPRXhjZXB0aW9uCAAWAQAZSW52YWxpZCBNTUMgc3RyaW5nIGZyYW1lLgoAEwAYDAAHABkBABUoTGphdmEvbGFuZy9TdHJpbmc7KVYKAAoAGwwAHAAdAQAJcmVhZEZ1bGx5AQAFKFtCKVYJAB8AIAcAIQwAIgAjAQAhamF2YS9uaW8vY2hhcnNldC9TdGFuZGFyZENoYXJzZXRzAQAFVVRGXzgBABpMamF2YS9uaW8vY2hhcnNldC9DaGFyc2V0OwoAJQAmBwAnDAAoACkBABhqYXZhL25pby9jaGFyc2V0L0NoYXJzZXQBAApuZXdEZWNvZGVyAQAjKClMamF2YS9uaW8vY2hhcnNldC9DaGFyc2V0RGVjb2RlcjsJACsALAcALQwALgAvAQAiamF2YS9uaW8vY2hhcnNldC9Db2RpbmdFcnJvckFjdGlvbgEABlJFUE9SVAEAJExqYXZhL25pby9jaGFyc2V0L0NvZGluZ0Vycm9yQWN0aW9uOwoAMQAyBwAzDAA0ADUBAB9qYXZhL25pby9jaGFyc2V0L0NoYXJzZXREZWNvZGVyAQAQb25NYWxmb3JtZWRJbnB1dAEARyhMamF2YS9uaW8vY2hhcnNldC9Db2RpbmdFcnJvckFjdGlvbjspTGphdmEvbmlvL2NoYXJzZXQvQ2hhcnNldERlY29kZXI7CgAxADcMADgANQEAFW9uVW5tYXBwYWJsZUNoYXJhY3RlcgoAOgA7BwA8DAA9AD4BABNqYXZhL25pby9CeXRlQnVmZmVyAQAEd3JhcAEAGShbQilMamF2YS9uaW8vQnl0ZUJ1ZmZlcjsKADEAQAwAQQBCAQAGZGVjb2RlAQAsKExqYXZhL25pby9CeXRlQnVmZmVyOylMamF2YS9uaW8vQ2hhckJ1ZmZlcjsKAEQARQcARgwARwBIAQATamF2YS9uaW8vQ2hhckJ1ZmZlcgEACHRvU3RyaW5nAQAUKClMamF2YS9sYW5nL1N0cmluZzsKAEoASwcATAwATQBOAQAQamF2YS9sYW5nL1N0cmluZwEAB2luZGV4T2YBAAQoSSlJCABQAQAZSW52YWxpZCBNTUMgTlVMIGFyZ3VtZW50LgoASgBSDABTAA4BAAZsZW5ndGgDABVqrAgAVgEAGkludmFsaWQgTU1DIGFyZ3YgZW52ZWxvcGUuCgBYAFkHAFoMAFsAXAEAEGphdmEvdXRpbC9CYXNlNjQBAA1nZXRVcmxEZWNvZGVyAQAcKClMamF2YS91dGlsL0Jhc2U2NCREZWNvZGVyOwoAXgBfBwBgDABBAGEBABhqYXZhL3V0aWwvQmFzZTY0JERlY29kZXIBABYoTGphdmEvbGFuZy9TdHJpbmc7KVtCBwBjAQAiamF2YS9sYW5nL0lsbGVnYWxBcmd1bWVudEV4Y2VwdGlvbgMAEBAACABmAQAhSW52YWxpZCBNTUMgYXJndiBlbnZlbG9wZSBsZW5ndGguBwBoAQAcamF2YS9pby9CeXRlQXJyYXlJbnB1dFN0cmVhbQoAZwBqDAAHAB0KAAoAbAwABwBtAQAYKExqYXZhL2lvL0lucHV0U3RyZWFtOylWA01NQzMIAHABABpVbmtub3duIE1NQyBhcmd2IGVudmVsb3BlLgMAEAAACABzAQAeSW52YWxpZCBNTUMgYXJndiBmcmFtZSBsZW5ndGguBwB1AQAWamF2YS91dGlsL3ppcC9JbmZsYXRlcgoAdAAFCgB0AHgMAHkAegEACHNldElucHV0AQAHKFtCSUkpVgoAdAB8DAB9AH4BAAhmaW5pc2hlZAEAAygpWgoAdACADACBAIIBAAdpbmZsYXRlAQAHKFtCSUkpSQgAhAEAIkludmFsaWQgTU1DIGNvbXByZXNzZWQgYXJndiBmcmFtZS4KAHQAhgwAhwAOAQAMZ2V0UmVtYWluaW5nCACJAQAjSW52YWxpZCBNTUMgY29tcHJlc3NlZCBhcmd2IGxlbmd0aC4KAHQAiwwAjAAIAQADZW5kBwCOAQAhamF2YS91dGlsL3ppcC9EYXRhRm9ybWF0RXhjZXB0aW9uCgBnAJAMAAcAegNNTUMyCACTAQAXVW5rbm93biBNTUMgYXJndiBmcmFtZS4KAAEAlQwAlgCXAQAKcmVhZFN0cmluZwEALShMamF2YS9pby9EYXRhSW5wdXRTdHJlYW07KUxqYXZhL2xhbmcvU3RyaW5nOwgAmQEAEnBjbGxmLm1tYy5ydW50aW1lLgoASgCbDACcAJ0BAApzdGFydHNXaXRoAQAVKExqYXZhL2xhbmcvU3RyaW5nOylaCACfAQAZSW52YWxpZCBNTUMgdGFyZ2V0IGNsYXNzLggAoQEAAlwuCgBKAKMMAKQApQEABXNwbGl0AQAoKExqYXZhL2xhbmcvU3RyaW5nO0kpW0xqYXZhL2xhbmcvU3RyaW5nOwoASgCnDACoAKkBAAZjaGFyQXQBAAQoSSlDCgCrAKwHAK0MAK4ArwEAE2phdmEvbGFuZy9DaGFyYWN0ZXIBABVpc0phdmFJZGVudGlmaWVyU3RhcnQBAAQoQylaCgCrALEMALIArwEAFGlzSmF2YUlkZW50aWZpZXJQYXJ0CAC0AQAXSW52YWxpZCBNTUMgYXJndiBjb3VudC4IALYBABtJbnZhbGlkIE1NQyBwcm9wZXJ0eSBjb3VudC4IALgBABpJbnZhbGlkIE1NQyBwcm9wZXJ0eSBuYW1lLggAugEAF1RyYWlsaW5nIE1NQyBhcmd2IGRhdGEuCgC8AL0HAL4MAL8AwAEAEGphdmEvbGFuZy9TeXN0ZW0BAAtzZXRQcm9wZXJ0eQEAOChMamF2YS9sYW5nL1N0cmluZztMamF2YS9sYW5nL1N0cmluZzspTGphdmEvbGFuZy9TdHJpbmc7CgDCAMMHAMQMAMUAxgEAFWphdmEvbGFuZy9DbGFzc0xvYWRlcgEAFGdldFN5c3RlbUNsYXNzTG9hZGVyAQAZKClMamF2YS9sYW5nL0NsYXNzTG9hZGVyOwoAwgDIDADJAMoBAAlsb2FkQ2xhc3MBACUoTGphdmEvbGFuZy9TdHJpbmc7KUxqYXZhL2xhbmcvQ2xhc3M7CADMAQAEbWFpbgcAzgEAD2phdmEvbGFuZy9DbGFzcwcA0AEAE1tMamF2YS9sYW5nL1N0cmluZzsKAM0A0gwA0wDUAQAJZ2V0TWV0aG9kAQBAKExqYXZhL2xhbmcvU3RyaW5nO1tMamF2YS9sYW5nL0NsYXNzOylMamF2YS9sYW5nL3JlZmxlY3QvTWV0aG9kOwoA1gDXBwDYDADZANoB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";
    public const string JavaSource = """
package pcllf.mmc.runtime;

import java.io.ByteArrayInputStream;
import java.io.DataInputStream;
import java.io.IOException;
import java.lang.reflect.InvocationTargetException;
import java.nio.ByteBuffer;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.util.Base64;
import java.util.zip.DataFormatException;
import java.util.zip.Inflater;

// Original LF host transport. Repository LICENCE; not upstream Wrapper code.
public final class ArgvBridge {
    private static final int MAX_FRAME = 1048576;
    private static final int MAX_STRING = 262144;
    private static final int MAX_ARGS = 4096;
    private static final int MAX_ENVELOPE = MAX_FRAME + 4096;
    private ArgvBridge() { }
    private static String readString(DataInputStream input) throws IOException {
        int length = input.readInt();
        if (length < 0 || length > MAX_STRING || length > input.available()) throw new IOException("Invalid MMC string frame.");
        byte[] bytes = new byte[length];
        input.readFully(bytes);
        String value = StandardCharsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(bytes)).toString();
        if (value.indexOf(0) >= 0) throw new IOException("Invalid MMC NUL argument.");
        return value;
    }
    public static void main(String[] packet) throws Throwable {
        if (packet.length != 1 || packet[0].length() > ((MAX_ENVELOPE + 2) / 3) * 4) throw new IOException("Invalid MMC argv envelope.");
        byte[] bytes;
        try { bytes = Base64.getUrlDecoder().decode(packet[0]); }
        catch (IllegalArgumentException invalid) { throw new IOException("Invalid MMC argv envelope."); }
        if (bytes.length > MAX_ENVELOPE || bytes.length < 8) throw new IOException("Invalid MMC argv envelope length.");
        DataInputStream envelope = new DataInputStream(new ByteArrayInputStream(bytes));
        if (envelope.readInt() != 0x4d4d4333) throw new IOException("Unknown MMC argv envelope.");
        int expected = envelope.readInt();
        if (expected < 0 || expected > MAX_FRAME) throw new IOException("Invalid MMC argv frame length.");
        byte[] plain = new byte[expected + 1];
        Inflater inflater = new Inflater();
        int position = 0;
        try {
            inflater.setInput(bytes, 8, bytes.length - 8);
            while (!inflater.finished()) {
                int count = inflater.inflate(plain, position, plain.length - position);
                position += count;
                if (position > expected || (count == 0 && !inflater.finished())) throw new IOException("Invalid MMC compressed argv frame.");
            }
            if (position != expected || inflater.getRemaining() != 0) throw new IOException("Invalid MMC compressed argv length.");
        } catch (DataFormatException invalid) { throw new IOException("Invalid MMC compressed argv frame."); }
        finally { inflater.end(); }
        DataInputStream input = new DataInputStream(new ByteArrayInputStream(plain, 0, expected));
        if (input.readInt() != 0x4d4d4332) throw new IOException("Unknown MMC argv frame.");
        String target = readString(input);
        if (target.length() == 0 || target.length() > 512 || target.startsWith("pcllf.mmc.runtime.")) throw new IOException("Invalid MMC target class.");
        for (String part : target.split("\\.", -1)) {
            if (part.length() == 0 || !Character.isJavaIdentifierStart(part.charAt(0))) throw new IOException("Invalid MMC target class.");
            for (int i = 1; i < part.length(); i++) if (!Character.isJavaIdentifierPart(part.charAt(i))) throw new IOException("Invalid MMC target class.");
        }
        int count = input.readInt();
        if (count < 0 || count > MAX_ARGS) throw new IOException("Invalid MMC argv count.");
        String[] arguments = new String[count];
        for (int i = 0; i < count; i++) arguments[i] = readString(input);
        int properties = input.readInt();
        if (properties < 0 || properties > 1024) throw new IOException("Invalid MMC property count.");
        String[] keys = new String[properties];
        String[] values = new String[properties];
        for (int i = 0; i < properties; i++) {
            keys[i] = readString(input); values[i] = readString(input);
            if (keys[i].length() == 0) throw new IOException("Invalid MMC property name.");
        }
        if (input.available() != 0) throw new IOException("Trailing MMC argv data.");
        for (int i = 0; i < properties; i++) System.setProperty(keys[i], values[i]);
        try {
            ClassLoader.getSystemClassLoader().loadClass(target).getMethod("main", String[].class).invoke(null, (Object) arguments);
        } catch (InvocationTargetException game) { throw game.getCause(); }
    }
}
""";
    public static byte[] CreateJarBytes() {
        byte[] classBytes = Convert.FromBase64String(ClassPayload);
        if (Convert.ToHexStringLower(SHA256.HashData(classBytes)) != ClassSha256) throw new InvalidOperationException("MMC bridge payload identity mismatch.");
        using MemoryStream output = new();
        using (ZipArchive archive = new(output, ZipArchiveMode.Create, true)) {
            var entry = archive.CreateEntry(EntryPoint.Replace('.', '/') + ".class", CompressionLevel.NoCompression);
            entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var body = entry.Open(); body.Write(classBytes);
        }
        return output.ToArray();
    }
    public static string JarSha256() => Convert.ToHexStringLower(SHA256.HashData(CreateJarBytes()));
    // The frame includes live account values. Never persist/log/hash it as non-sensitive data.
    public static string Encode(string targetClass, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? restoredProperties = null) {
        ArgumentNullException.ThrowIfNull(targetClass); ArgumentNullException.ThrowIfNull(arguments);
        if (targetClass.Length is < 1 or > 512 || targetClass.StartsWith("pcllf.mmc.runtime.", StringComparison.Ordinal) || arguments.Count > MaximumArgumentCount || (restoredProperties?.Count ?? 0) > 1024)
            throw new ArgumentException("Invalid MMC runtime target/count.");
        using MemoryStream frame = new(); WriteInteger(frame, 0x4d4d4332); WriteString(frame, targetClass); WriteInteger(frame, arguments.Count);
        foreach (string argument in arguments) WriteString(frame, argument);
        WriteInteger(frame, restoredProperties?.Count ?? 0);
        if (restoredProperties is not null) foreach (var property in restoredProperties) {
            if (string.IsNullOrEmpty(property.Key)) throw new ArgumentException("Invalid MMC property name.");
            WriteString(frame, property.Key); WriteString(frame, property.Value);
        }
        using MemoryStream envelope = new();
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header, 0x4d4d4333);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], checked((int)frame.Length));
        envelope.Write(header);
        using (ZLibStream compressed = new(envelope, CompressionLevel.SmallestSize, leaveOpen: true)) {
            frame.Position = 0; frame.CopyTo(compressed);
        }
        if (envelope.Length > MaximumCompressedEnvelopeBytes) throw new ArgumentException("MMC compressed runtime envelope exceeds its byte budget.");
        return Convert.ToBase64String(envelope.ToArray()).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
    private static void WriteString(Stream stream, string value) {
        if (value is null || value.Contains('\0')) throw new ArgumentException("Invalid NUL/null MMC runtime argument.");
        var utf8 = new UTF8Encoding(false, true); int length = utf8.GetByteCount(value);
        if (length > MaximumStringBytes || stream.Length > MaximumFrameBytes - 4L - length) throw new ArgumentException("MMC runtime string/frame exceeds its byte budget.");
        WriteInteger(stream, length); stream.Write(utf8.GetBytes(value));
    }
    private static void WriteInteger(Stream stream, int value) {
        if (stream.Length > MaximumFrameBytes - 4L) throw new ArgumentException("MMC runtime frame exceeds its byte budget.");
        Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, value); stream.Write(bytes);
    }
}
