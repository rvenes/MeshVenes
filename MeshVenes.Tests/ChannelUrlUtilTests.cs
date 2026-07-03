using Google.Protobuf;
using Meshtastic.Protobufs;
using MeshVenes.Services;
using Xunit;

namespace MeshVenes.Tests;

public class ChannelUrlUtilTests
{
    private static ChannelSet SampleChannelSet()
    {
        var set = new ChannelSet
        {
            LoraConfig = new Config.Types.LoRaConfig
            {
                UsePreset = true,
                ModemPreset = Config.Types.LoRaConfig.Types.ModemPreset.LongFast,
                Region = Config.Types.LoRaConfig.Types.RegionCode.Eu868
            }
        };
        set.Settings.Add(new ChannelSettings { Name = "Primary", Psk = ByteString.CopyFrom(new byte[] { 1 }) });
        set.Settings.Add(new ChannelSettings { Name = "Team", Psk = ByteString.CopyFrom(new byte[16]) });
        return set;
    }

    [Fact]
    public void BuildShareUrl_RoundTripsThroughParse()
    {
        var original = SampleChannelSet();

        var url = ChannelUrlUtil.BuildShareUrl(original);
        Assert.StartsWith("https://meshtastic.org/e/#", url);

        Assert.True(ChannelUrlUtil.TryParseShareUrl(url, out var parsed, out var addMode));
        Assert.False(addMode);
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void BuildShareUrl_AddMode_SetsQueryAndRoundTrips()
    {
        var url = ChannelUrlUtil.BuildShareUrl(SampleChannelSet(), addMode: true);
        Assert.Contains("?add=true", url);

        Assert.True(ChannelUrlUtil.TryParseShareUrl(url, out _, out var addMode));
        Assert.True(addMode);
    }

    [Fact]
    public void TryParseShareUrl_AcceptsBareBase64Payload()
    {
        var payload = ChannelUrlUtil.ToBase64Url(System.Convert.ToBase64String(SampleChannelSet().ToByteArray()));

        Assert.True(ChannelUrlUtil.TryParseShareUrl(payload, out var parsed, out _));
        Assert.Equal(2, parsed.Settings.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://meshtastic.org/e/#not-base64!!!")]
    [InlineData("https://example.com/no-fragment")]
    public void TryParseShareUrl_RejectsInvalidInput(string input)
    {
        Assert.False(ChannelUrlUtil.TryParseShareUrl(input, out _, out _));
    }

    [Fact]
    public void ToReplacementChannels_MapsRolesAndDisablesRemainder()
    {
        var set = SampleChannelSet();

        var channels = ChannelUrlUtil.ToReplacementChannels(set.Settings);

        Assert.Equal(8, channels.Count);
        Assert.Equal(Channel.Types.Role.Primary, channels[0].Role);
        Assert.Equal("Primary", channels[0].Settings.Name);
        Assert.Equal(Channel.Types.Role.Secondary, channels[1].Role);
        for (var i = 2; i < 8; i++)
        {
            Assert.Equal(Channel.Types.Role.Disabled, channels[i].Role);
            Assert.Equal(i, channels[i].Index);
        }
    }

    [Theory]
    [InlineData("YQ", "YQ==")]
    [InlineData("YWI", "YWI=")]
    [InlineData("YWJj", "YWJj")]
    public void FromBase64Url_RestoresPadding(string input, string expected)
    {
        Assert.Equal(expected, ChannelUrlUtil.FromBase64Url(input));
    }
}
