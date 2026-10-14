using System.Text;
using RagAssistant.Modules.Documents.Common;

namespace RagAssistant.Modules.Documents.Tests;

internal sealed class DocumentTextTests
{
    [Test]
    [Arguments("a\r\nb", "a\nb")]
    [Arguments("a\rb", "a\nb")]
    [Arguments("a\r\n\r\nb\r", "a\n\nb\n")]
    [Arguments("﻿hello", "hello")]
    [Arguments("﻿# T\r\nx", "# T\nx")]
    [Arguments("plain\n", "plain\n")]
    public async Task Normalize_variousInputs_stripsBomAndUsesLf(string input, string expected)
    {
        await Assert.That(DocumentText.Normalize(input)).IsEqualTo(expected);
    }

    [Test]
    public async Task Hash_knownInput_returnsLowercaseSha256Hex()
    {
        await Assert.That(DocumentText.Hash("abc"))
            .IsEqualTo("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    [Test]
    public async Task Hash_crlfAndBomVariants_matchOnceNormalized()
    {
        var plain = DocumentText.Hash(DocumentText.Normalize("# T\nbody"));
        var variant = DocumentText.Hash(DocumentText.Normalize("﻿# T\r\nbody"));

        await Assert.That(variant).IsEqualTo(plain);
    }

    [Test]
    public async Task TryDecode_validUtf8_returnsText()
    {
        var ok = DocumentText.TryDecode(Encoding.UTF8.GetBytes("café ✓"), out var text);

        await Assert.That(ok).IsTrue();
        await Assert.That(text).IsEqualTo("café ✓");
    }

    [Test]
    public async Task TryDecode_invalidUtf8_fails()
    {
        // 0xFF can never start a UTF-8 sequence; Latin-1 "é" (0xE9) followed by a space is also invalid.
        await Assert.That(DocumentText.TryDecode([0x61, 0xFF, 0x62], out _)).IsFalse();
        await Assert.That(DocumentText.TryDecode([0xE9, 0x20], out _)).IsFalse();
    }
}
