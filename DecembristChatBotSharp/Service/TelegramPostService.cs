﻿using System.Text.RegularExpressions;
using HtmlAgilityPack;
using JasperFx.Core;
using Lamar;
using Serilog;

namespace DecembristChatBotSharp.Service;

public record TelegramRandomMeme(string PhotoLink);

[Singleton]
public class TelegramPostService(
    AppConfig appConfig, 
    IHttpClientFactory httpClientFactory,
    Random random)
{
    private const string TelegramChannelUrlFormat = "https://t.me/s/{0}";
    private const string TelegramPostUrlFormat = "https://t.me/{0}/{1}?embed=1&mode=tme";

    private readonly Regex _backgroundImageRegex = new(@"background-image:url\('(?<url>.*?)'\)");

    private readonly int _maxGetPostRetries = appConfig.CommandConfig.TelegramPostConfig.MaxGetPostRetries;
    private readonly string[] _channelNames = appConfig.CommandConfig.TelegramPostConfig.ChannelNames;
    private readonly int _scanPostCount = appConfig.CommandConfig.TelegramPostConfig.ScanPostCount;

    public async Task<Option<TelegramRandomMeme>> GetRandomPostPicture()
    {
        var randomChannel = _channelNames[random.Next(0, _channelNames.Length)];

        var postId = await GetLastPostId(randomChannel);
        if (postId.IsNone)
        {
            Log.Error("Failed to get last post id for channel {0}", randomChannel);
            return None;
        }
        
        for (var i = 0; i < _maxGetPostRetries; i++)
        {
            var maybeMeme = await GetPostPicture(postId, randomChannel);

            if (maybeMeme.IsSome()) return maybeMeme.ToOption();
            maybeMeme.IfFail(ex =>
                Log.Error(ex, "Failed to get random post {0} picture for telegram channel {1}", postId, randomChannel));
        }

        return None;
    }

    private async Task<TryOption<TelegramRandomMeme>> GetPostPicture(Option<int> postId, string randomChannel)
    {
        var maybePostUrl =
            from id in postId
            let firstPostId = Math.Max(1, id - _scanPostCount)
            let randomId = random.Next(firstPostId, id + 1)
            let postUrl = string.Format(TelegramPostUrlFormat, randomChannel, randomId)
            select postUrl;

        var maybeHtml =
            from postUrl in maybePostUrl.ToTryOptionAsync()
            let httpClient = httpClientFactory.CreateClient()
            from html in httpClient.GetStringAsync(postUrl).ToTryOption()
            select html;

        var maybeMeme =
            from html in await maybeHtml
            let wrapMode = GetWrapMode(html)
            where wrapMode != null
            let style = wrapMode.GetAttributeValue("style", "")
            let match = _backgroundImageRegex.Match(style)
            where match.Success
            let imageUrl = match.Groups["url"].Value
            where !string.IsNullOrEmpty(imageUrl)
            select new TelegramRandomMeme(imageUrl);
        
        return maybeMeme;
    }

    private async Task<Option<int>> GetLastPostId(string channel)
    {
        var url = string.Format(TelegramChannelUrlFormat, channel);

        var httpClient = new HttpClient();
        var html = await httpClient.GetStringAsync(url);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var messages = doc.DocumentNode.SelectNodes("//div[contains(@class, 'tgme_widget_message')]");
        var lastMessage = messages.LastOrNone();
        return
            from message in lastMessage
            where message != null
            let node = message.SelectSingleNode(".//a[contains(@class, 'tgme_widget_message_date')]")
            where node != null
            let href = node.GetAttributeValue("href", "")
            where href.IsNotEmpty() && href.Contains($"/{channel}/")
            let parts = href.Split('/')
            where parts.Length > 0
            let id = int.Parse(parts.Last())
            where id > 0
            select id;
    }

    private HtmlNode GetWrapMode(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return doc.DocumentNode.SelectSingleNode("//a[contains(@class, 'tgme_widget_message_photo_wrap')]");
    }
}