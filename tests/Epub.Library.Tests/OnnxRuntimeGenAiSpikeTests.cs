using FluentAssertions;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace Epub.Library.Tests;

/// <summary>
/// S6.0 spike: prove that Microsoft.ML.OnnxRuntimeGenAI's native runtime
/// loads on Windows ARM64 in this process. Cheap probe — we deliberately
/// hand it a non-existent model path and confirm the failure mode is
/// "model path doesn't exist" rather than "native binary couldn't load".
/// If this passes, the actual model fetch (S6.1, ~2.73 GB Phi-3-mini-4k
/// CPU INT4) is just a download.
/// </summary>
public sealed class OnnxRuntimeGenAiSpikeTests
{
    [Fact]
    public void Model_BadPath_ThrowsModelLoadError_NotDllNotFound()
    {
        var act = () => new Model("c:/this/path/does/not/exist/for/the/spike");

        var ex = act.Should().Throw<Exception>(
            "constructing a Model with a missing path must error somehow").Which;

        ex.Should().NotBeOfType<DllNotFoundException>(
            "DllNotFoundException would mean the win-arm64 onnxruntime-genai.dll didn't load — " +
            "fall back to LLamaSharp if this fails");
        ex.Should().NotBeOfType<BadImageFormatException>(
            "BadImageFormatException would mean the native binary is wrong-arch (e.g. x64 binary " +
            "loaded into ARM64 process)");
    }
}
