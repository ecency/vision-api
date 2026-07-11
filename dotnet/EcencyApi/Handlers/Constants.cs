using System.Text.Json.Nodes;

namespace EcencyApi.Handlers;

/// <summary>Port of src/server/handlers/constants.ts.</summary>
public static class Constants
{
    public static readonly string[] Bots =
    {
        "amazingdrinks",
        "asean.hive",
        "bbhbot",
        "bdcommunity",
        "beerlover",
        "boomerang",
        "booster",
        "buildawhale",
        "curation-cartel",
        "curangel",
        "c-squared",
        "dhedge",
        "discovery-it",
        "dookbot",
        "dsc-r2cornell",
        "gifbot",
        "hive-177745",
        "hivebits",
        "hivebuzz",
        "hive-br.voter",
        "hivecurators",
        "hivegifbot",
        "hivepakistan",
        "hive-lu",
        "hiq.smartbot",
        "hk-gifts",
        "hug.bot",
        "indiaunited",
        "indeedly",
        "kennybrown",
        "ladytoken",
        "lolzbot",
        "luvshares",
        "meme.bot",
        "neoxian-city",
        "pgm-curator",
        "pinmapple",
        "pixresteemer",
        "pizzabot",
        "poshthreads",
        "poshtoken",
        "promobot",
        "sloth.buzz",
        "sneaky-ninja",
        "splinterboost",
        "steem-bet",
        "steemitboard",
        "steemstem",
        "steem-ua",
        "steemium",
        "steem-plus",
        "terraboost",
        "tipu",
        "thepimpdistrict",
        "upmewhale",
        "upvotebank",
        "utopian-io",
        "visualbot",
        "warofclans",
        "weed.dispenser",
        "worldmappin",
        "wine.bot",
        "youarealive",
    };

    public static JsonArray BotsJson()
    {
        var arr = new JsonArray();
        foreach (var b in Bots)
        {
            arr.Add(b);
        }
        return arr;
    }

    public const int ActiveProposalId = 336;

    public static readonly IReadOnlyDictionary<string, string> AssetIconUrls =
        new Dictionary<string, string>
        {
            ["POINTS"] = "https://images.ecency.com/DQmRhnpQt7zzfS67uDGv6TPLGmKFhWfNFrDLECP4M3bRo4f/ecency_logo_2x.png",
            ["HIVE"] = "https://images.ecency.com/DQmb7w4JNXXPKyzYVk615xGSfMTKLArQ7R9kWapuiY8dNHW/hive_icon.png",
            ["HBD"] = "https://images.ecency.com/DQmbXwGcbDvATn7x7XpZVPuYhB8YYTozDDvNtJanVpbdE8f/hbd_icon.png",
            ["SPK"] = "https://images.ecency.com/DQmZKETm7FX3HPmbohY2EEXDy3CebxZg8ESqU2ixVC77aA2/image.png",
            ["SPK_PLACEHOLDER"] = "https://images.ecency.com/DQmbUoMBYahwwaZ795LV1T19Nm6nWQ5EpD9NCV9jqEnY92b/image_1_.png",
            ["ENGINE_PLACEHOLDER"] = "https://images.ecency.com/DQmUWpEYA5f2U9NmCEheSdzXwqQrBTL16jG6vWBJGYjUtZB/image_2_.png",
            ["CHAIN_PLACEHOLDER"] = "https://images.ecency.com/DQmNhVLfUxifjFQidjj9BN8kbTBcp36LSoqR1zYauUMnJEC/chain_placeholder.png",
            ["BNB"] = "https://images.ecency.com/DQmezqZDiHN1NKYsLBWYwvBQ1mNhypkoe5w4JPwjeC3E4xH/bnb.png",
            ["BTC"] = "https://images.ecency.com/DQmPKC8rkqjCdMLMtwayQ81Mj62pjVBj81ZZoZBVdiJSeeH/btc.png",
            ["SOL"] = "https://images.ecency.com/DQmZJJmjz38nQGwNDG7Fuo6dHbS6P29SN7FcNy1RcSbWaj1/solana.png",
            ["ETH"] = "https://images.ecency.com/DQmebRVg81CP1kMeSEsMm8JHDMCu5Vkac5ndUHH4ppGSFU6/eth.png",
        };
}
