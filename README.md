# CodeBrix.Audio.Opus

Fully managed Ogg Opus decoding and encoding for [CodeBrix.Audio](https://github.com/ellisnet/CodeBrix.Audio), with no native binaries on any platform.
CodeBrix.Audio.Opus depends only on .NET and CodeBrix.Audio, and is provided as a .NET 10 library and associated `CodeBrix.Audio.Opus.BsdLicenseForever` NuGet package.

CodeBrix.Audio.Opus supports applications and assemblies that target Microsoft .NET version 10.0 and later.
Microsoft .NET version 10.0 is a Long-Term Supported (LTS) version of .NET, and was released on Nov 11, 2025; and will be actively supported by Microsoft until Nov 14, 2028.
Please update your C#/.NET code and projects to the latest LTS version of Microsoft .NET.

## Why this is a separate package

CodeBrix.Audio holds a deliberate licence bar of MIT or more permissive, which is what its
`CodeBrix.Audio.MitLicenseForever` package id promises. Opus cannot meet it: every managed Opus
implementation is a port of libopus, and libopus is BSD-3-Clause. So Opus support ships here
instead, under BSD-3-Clause, as an add-on. Add this package and CodeBrix.Audio's promise is
unchanged; skip it and nothing about CodeBrix.Audio changes either.

## One call turns it on

```csharp
using CodeBrix.Audio.Opus;

CodeBrixAudioOpus.Register();   // once, at application start-up
```

After that, `.opus` files play through `AudioFilePlayer`, `SoundEffectClip`, `WaveOutEvent`, the
CodeBrix.Platform AudioPlayer add-in and the CodeBrix.Platform GameEngine, open by file name
through `AudioFileReader`, and can be recorded by the engine's `Recorder` — none of which needs
any other change. **The application takes the dependency and makes the call; the add-ins never
do.**

There is deliberately no module initializer doing this for you: a module initializer only runs
once something in the assembly is touched, which trimming and lazy assembly loading make
unreliable, so the package would work in a debug build and silently fail in a trimmed publish.

## CodeBrix.Audio.Opus supports:

* Decoding and playing Ogg Opus (`.opus`) — voice notes, podcasts, `yt-dlp` output, Wikimedia audio
* Encoding to Ogg Opus, from any input sample rate
* Recording straight to `.opus` through the CodeBrix.Audio engine's `Recorder`
* Exact seeking, and durations that account for the encoder's pre-skip
* Reading and writing Opus tags (`TITLE`, `ARTIST`, and the rest)
* Mono and stereo — Opus channel mapping family 0, which covers ordinary Opus files
* Every platform CodeBrix.Audio runs on, including linux-riscv64: **there is no native code here**

## Sample Code

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
CodeBrixAudioOpus.Register();

using var stream = File.Create("recording.opus");
var recorder = new Recorder(captureDevice, stream, "opus");
recorder.StartRecording();
```

## Sharp edges

* **Dispose the writer.** Like `WaveFileWriter`, `OpusFileWriter` only produces a complete,
  correctly-described file on `Dispose()`: the last partial frame is padded and flushed there, and
  the closing page records the true sample count so a decoder trims that padding instead of
  playing it. An undisposed writer leaves a file missing its tail that misreports its length.
* **48 kHz is not negotiable on the way out.** Opus decodes at 48 kHz whatever the file says. The
  rate in an Opus header is the rate the *encoder* was handed — commonly 16000 for a messenger
  voice note — and RFC 7845 marks it informational. `OpusFileReader.WaveFormat` is therefore
  always 48 kHz; the declared value is available as `EncoderInputSampleRate`.
* **Mono and stereo only.** Channel mapping family 0. Multichannel Opus (family 1) is declined
  with a clear message rather than mis-mapped.

## License

The project is licensed under the BSD 3-Clause License. see: https://en.wikipedia.org/wiki/BSD_licenses

The Opus codec itself is vendored from the Concentus project and carries the same BSD-3-Clause
terms; `THIRD-PARTY-NOTICES.txt` reproduces that licence in full, names every copyright holder,
and records exactly what was vendored and what was changed.
