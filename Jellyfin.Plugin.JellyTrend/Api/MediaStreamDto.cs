namespace Jellyfin.Plugin.JellyTrend.Api;

/// <summary>
/// Data transfer object describing a single media stream.
/// </summary>
public sealed class MediaStreamDto
{
    /// <summary>
    /// Gets or sets the stream type (Audio, Video or Subtitle).
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// Gets or sets the codec.
    /// </summary>
    public string? Codec { get; set; }

    /// <summary>
    /// Gets or sets the language.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Gets or sets the display title.
    /// </summary>
    public string? DisplayTitle { get; set; }

    /// <summary>
    /// Gets or sets the channel count.
    /// </summary>
    public int? Channels { get; set; }

    /// <summary>
    /// Gets or sets the bit rate.
    /// </summary>
    public int? BitRate { get; set; }

    /// <summary>
    /// Gets or sets the sample rate.
    /// </summary>
    public int? SampleRate { get; set; }

    /// <summary>
    /// Gets or sets the video width.
    /// </summary>
    public int? Width { get; set; }

    /// <summary>
    /// Gets or sets the video height.
    /// </summary>
    public int? Height { get; set; }
}
