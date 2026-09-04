# CodeBrix.Audio.Opus

Fully managed Opus decoding and encoding for [CodeBrix.Audio](https://github.com/ellisnet/CodeBrix.Audio), with no native binaries on any platform - both Ogg Opus (`.opus`) files and the bare Opus packets a media container carries.
CodeBrix.Audio.Opus depends only on .NET and CodeBrix.Audio, and is provided as a .NET 10 library and associated `CodeBrix.Audio.Opus.BsdLicenseForever` NuGet package.

CodeBrix.Audio.Opus supports applications and assemblies that target Microsoft .NET version 10.0 and later.
Microsoft .NET version 10.0 is a Long-Term Supported (LTS) version of .NET, and was released on Nov 11, 2025; and will be actively supported by Microsoft until Nov 14, 2028.
Please update your C#/.NET code and projects to the latest LTS version of Microsoft .NET.

## Installation

```
dotnet add package CodeBrix.Audio.Opus.BsdLicenseForever
```

Note that the NuGet package ID and the namespace are different - there is no package named plain `CodeBrix.Audio.Opus`:

* NuGet package ID: `CodeBrix.Audio.Opus.BsdLicenseForever`
* Assembly and primary namespace: `CodeBrix.Audio.Opus` - i.e. `using CodeBrix.Audio.Opus;`

XML documentation (IntelliSense) ships alongside the assembly.

The package pulls in the following automatically; no version pinning is needed in the consuming project:

* `CodeBrix.Audio.MitLicenseForever` - the audio engine this package adds Opus to. Note that this package is licensed under the MIT License, while CodeBrix.Audio.Opus is BSD 3-Clause.

There is nothing else to add - no native-asset package, and no platform-specific payload.

## CodeBrix.Audio.Opus supports:

* Decoding and playing Ogg Opus (`.opus`) - voice notes, podcasts, `yt-dlp` output, Wikimedia audio
* Decoding the bare Opus packets a media container carries, through the CodeBrix.Audio engine's packet seam: `OpusPacketCodecFactory` is what teaches that seam Opus, and one `Register()` call installs it
* Packet-loss concealment on that packet path, so a source that drops packets is filled in rather than clicking
* Encoding to Ogg Opus, from any input sample rate
* Recording straight to `.opus` through the CodeBrix.Audio engine's `Recorder`
* Exact seeking, and durations that account for the encoder's pre-skip
* Reading and writing Opus tags (`TITLE`, `ARTIST`, and the rest)
* Mono and stereo - Opus channel mapping family 0, which covers ordinary Opus files
* Every platform CodeBrix.Audio runs on, including linux-riscv64: **there is no native code here**

## Why this is a separate package

CodeBrix.Audio holds a deliberate licence bar of MIT or more permissive, which is what its
`CodeBrix.Audio.MitLicenseForever` package id promises. This package is BSD 3-Clause, which that
bar does not admit, so Opus support ships here as an add-on rather than inside CodeBrix.Audio. Add
this package and CodeBrix.Audio's promise is unchanged; skip it and nothing about CodeBrix.Audio
changes either.

## Sample Code

### One call turns it on

```csharp
using CodeBrix.Audio.Opus;

CodeBrixAudioOpus.Register();   // once, at application start-up
```

After that, `.opus` files play through `AudioFilePlayer`, `SoundEffectClip`, `WaveOutEvent`, the
CodeBrix.Platform AudioPlayer add-in and the CodeBrix.Platform GameEngine, open by file name
through `AudioFileReader`, and can be recorded by the engine's `Recorder`; and Opus packets lifted
out of a media container decode through the engine's packet seam - none of which needs any other
change. **The application takes the dependency and makes the call; the add-ins never do.**

`Register(AudioEngine)` does the same two registrations on an engine you drive yourself, rather
than on the shared output.

There is deliberately no module initializer doing this for you: a module initializer only runs
once something in the assembly is touched, which trimming and lazy assembly loading make
unreliable, so the package would work in a debug build and silently fail in a trimmed publish.

### Play a .opus file

```csharp
using CodeBrix.Audio.Opus;
using CodeBrix.Audio.Playback;

CodeBrixAudioOpus.Register();          // once, at start-up

var media = new AudioFilePlayer();
media.Load("voice-note.opus");         // Duration is available as soon as Load returns
media.Play();
```

### Read a .opus file as float samples

```csharp
using CodeBrix.Audio.Opus;

using var reader = new OpusFileReader("music.opus");
// reader.WaveFormat is 48 kHz 32-bit IEEE float - Opus decodes at 48 kHz, always.
// reader.EncoderInputSampleRate is what the ENCODER was given (often 16000 for a voice
// note); it is informational and never used to convert anything.

var buffer = new byte[reader.WaveFormat.AverageBytesPerSecond];
int read;
while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
{
    // interleaved 32-bit floats in [-1, 1]
}
```

### Write a .opus file

```csharp
using CodeBrix.Audio.Opus;

var options = new OpusFileWriterOptions
{
    Bitrate = 32_000,
    Profile = OpusEncodingProfile.Voice   // Music is the default
};
options.Tags["TITLE"] = "Voice memo";

using (var writer = new OpusFileWriter("memo.opus", sampleRate: 44100, channels: 1, options))
{
    writer.Write(samples);   // any input rate; resampled to 48 kHz on the way in
}
// Dispose is what finishes the file - see the note below.
```

### Record straight to .opus

```csharp
using System.IO;
using CodeBrix.Audio.Engine.Components;
using CodeBrix.Audio.Opus;

CodeBrixAudioOpus.Register();

// captureDevice is a CodeBrix.Audio.Engine.Abstracts.Devices.AudioCaptureDevice
using var stream = File.Create("recording.opus");
var recorder = new Recorder(captureDevice, stream, "opus");
recorder.StartRecording();
```

### Decode Opus packets from a container

```csharp
using CodeBrix.Audio.Opus;
using CodeBrix.Audio.Playback;
using CodeBrix.Audio.Wave;

CodeBrixAudioOpus.Register();           // the same call registers the packet seam
SharedAudioOutput.Configure(48000);     // Opus's only rate

var player = new PacketAudioPlayer();
player.Open("opus", codecPrivate, packetSource);   // your demultiplexer supplies both
player.Play();

// ...or, to decode packets without playing them:
using var decoder = SharedAudioOutput.CreatePacketDecoder("opus", codecPrivate);
```

The codec id is `"opus"` - the codec, not the container - and `codecPrivate` is the Opus
identification header exactly as the container stored it; a Matroska or WebM track holds those
bytes verbatim, so there is nothing to unwrap. `packetSource` is CodeBrix.Audio's
`IAudioPacketSource`, which your demultiplexer implements.

## Sharp edges

* **Dispose the writer.** Like `WaveFileWriter`, `OpusFileWriter` only produces a complete,
  correctly-described file once it is finished: the last partial frame is padded and flushed there,
  and the closing page records the true sample count so a decoder trims that padding instead of
  playing it. `Dispose()` does that for you (`Finish()` is the explicit form); an undisposed writer
  leaves a file missing its tail that misreports its length.
* **48 kHz is not negotiable on the way out.** Opus decodes at 48 kHz whatever the file says. The
  rate in an Opus header is the rate the *encoder* was handed - commonly 16000 for a messenger
  voice note - and RFC 7845 marks it informational. `OpusFileReader.WaveFormat` is therefore
  always 48 kHz; the declared value is available as `EncoderInputSampleRate`.
* **The header's output gain is applied.** RFC 7845 requires a decoder to apply the identification
  header's output gain, and both paths - the file reader and the packet decoder - do. The field is
  0 in practically every file ordinary encoders produce, so this is invisible for almost all audio;
  a file that does carry a gain plays at the level its author asked for, and asks nothing of you.
* **Mono and stereo only.** Channel mapping family 0, on both paths. Multichannel Opus (family 1)
  is declined with a clear message rather than mis-mapped.

## Documentation

The NuGet package includes `AGENT-README.txt`, a complete API reference and usage guide written for AI coding agents - point your agent at that file when it is writing code against this library.

The packet seam's own types - `IAudioPacketSource`, `PacketAudioPlayer` and the packet decoder interfaces - belong to CodeBrix.Audio; read that package's `AGENT-README.txt` for them. This package supplies the Opus codec they resolve.

Additional sample code and usage examples are available in the `CodeBrix.Audio.Opus.Tests` project:
https://github.com/ellisnet/CodeBrix.Audio.Opus/tree/main/tests/CodeBrix.Audio.Opus.Tests

## License

CodeBrix.Audio.Opus is licensed under the BSD 3-Clause License - see the
[LICENSE](https://github.com/ellisnet/CodeBrix.Audio.Opus/blob/main/LICENSE) file.

For licensing and provenance information about the open source code included in
this package, see [THIRD-PARTY-NOTICES.txt](https://github.com/ellisnet/CodeBrix.Audio.Opus/blob/main/THIRD-PARTY-NOTICES.txt).
