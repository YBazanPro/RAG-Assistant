using System.ComponentModel.DataAnnotations;

namespace RagAssistant.Modules.Documents.Options;

internal sealed class PdfOptions
{
    public const string Section = "Documents:Pdf";

    [Range(1, int.MaxValue)]
    public int MaxPages { get; set; } = 2000;

    public TimeSpan ExtractionTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>A page "has text" with at least this many non-whitespace characters.</summary>
    [Range(1, int.MaxValue)]
    public int MinCharsPerTextPage { get; set; } = 20;

    /// <summary>Below this share of pages with text a PDF counts as scanned and needs OCR.</summary>
    [Range(0.001, 1.0)]
    public double MinTextPageRatio { get; set; } = 0.10;

    public bool RemoveRepeatedMargins { get; set; } = true;

    public bool LayoutAnalysis { get; set; } = true;

    [Range(1.0, 10.0)]
    public double HeadingMinSizeRatio { get; set; } = 1.15;

    [Range(1, 6)]
    public int MaxHeadingLevels { get; set; } = 3;

    [Range(1, int.MaxValue)]
    public int MaxHeadingChars { get; set; } = 120;

    /// <summary>Pages read at the start of the file, and about as many spread over the rest, by the upload probe.</summary>
    [Range(1, int.MaxValue)]
    public int ProbePages { get; set; } = 10;

    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A text-less page with an image covering at least this share of its area looks scanned.</summary>
    [Range(0.001, 1.0)]
    public double ScannedImageCoverage { get; set; } = 0.8;

    /// <summary>Above this share of replacement or symbol characters the text layer is unusable.</summary>
    [Range(0.001, 1.0)]
    public double MaxGarbageRatio { get; set; } = 0.30;

    public bool HasPositiveDurations => ExtractionTimeout > TimeSpan.Zero && ProbeTimeout > TimeSpan.Zero;
}
