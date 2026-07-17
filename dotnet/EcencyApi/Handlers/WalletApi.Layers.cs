using System.Globalization;
using System.Text.Json.Nodes;
using EcencyApi.Infrastructure;

namespace EcencyApi.Handlers;

/// <summary>
/// Port of wallet-api.ts lines 800-1747: portfolio-item construction and the
/// per-layer builders (points, hive, engine, spk, chain). Output JSON preserves
/// the exact property insertion order of the TS objects (JSON.stringify drops
/// undefined-valued keys, so optional keys are added only when present).
/// </summary>
public static partial class WalletApi
{
    internal sealed class ItemOptions
    {
        public string? Address;
        public string? Error;
        public double? PendingRewards;
        public double? Savings;
        public double? Staked;
        public double? Apr;
    }

    internal static JsonObject MakePortfolioItem(
        string name, string symbol, string layer, double balance, double fiatRate,
        string currency, int precision, ItemOptions? options = null,
        string? iconUrl = null, JsonArray? actions = null, JsonArray? extraData = null)
    {
        options ??= new ItemOptions();

        var normalizedBalance = double.IsFinite(balance) ? balance : 0;
        var normalizedRate = double.IsFinite(fiatRate) ? fiatRate : 0;
        var hasSavings = options.Savings.HasValue && double.IsFinite(options.Savings.Value);
        var savingsValue = hasSavings ? options.Savings!.Value : 0;
        var hasStaked = options.Staked.HasValue && double.IsFinite(options.Staked.Value);
        var stakedValue = hasStaked ? options.Staked!.Value : 0;
        var totalBalance = normalizedBalance + (hasSavings ? savingsValue : 0) + (hasStaked ? stakedValue : 0);
        var hasPendingRewards = options.PendingRewards.HasValue && double.IsFinite(options.PendingRewards.Value);
        var normalizedPendingRewards = hasPendingRewards ? options.PendingRewards!.Value : 0;
        var hasApr = options.Apr.HasValue && double.IsFinite(options.Apr.Value);

        var item = new JsonObject
        {
            ["name"] = name,
            ["symbol"] = symbol,
            ["layer"] = layer,
            ["balance"] = totalBalance,
            ["fiatRate"] = normalizedRate,
            ["currency"] = currency.ToUpperInvariant(),
            ["precision"] = precision,
        };
        // iconUrl / actions / extraData: keys present in the TS literal but
        // JSON.stringify omits them when the value is undefined.
        if (iconUrl != null) item["iconUrl"] = iconUrl;
        if (actions != null) item["actions"] = actions.DeepClone();
        if (extraData != null) item["extraData"] = extraData.DeepClone();
        if (!string.IsNullOrEmpty(options.Error)) item["error"] = options.Error;
        if (!string.IsNullOrEmpty(options.Address)) item["address"] = options.Address;
        if (hasPendingRewards)
        {
            item["pendingRewards"] = normalizedPendingRewards;
            item["pendingRewardsFiat"] = normalizedPendingRewards * normalizedRate;
        }
        if (hasApr) item["apr"] = options.Apr!.Value;

        item["liquid"] = normalizedBalance;
        item["liquidFiat"] = normalizedBalance * normalizedRate;

        if (hasSavings)
        {
            item["savings"] = savingsValue;
            item["savingsFiat"] = savingsValue * normalizedRate;
        }
        if (hasStaked)
        {
            item["staked"] = stakedValue;
            item["stakedFiat"] = stakedValue * normalizedRate;
        }

        return item;
    }

    internal static double VestsToHivePower(double vests, double hivePerMVests)
    {
        if (!double.IsFinite(vests) || !double.IsFinite(hivePerMVests)) return 0;
        return (vests / 1e6) * hivePerMVests;
    }

    // parseToken(node || "fallback")
    private static double PToken(JsonNode? node, string fallback) =>
        HiveExplorer.ParseToken(JsJson.IsTruthy(node) ? node : JsonValue.Create(fallback));

    // JS Number(node): string -> NumberCoerce, number -> value, else NaN/0/1
    private static double NumberOf(JsonNode? node)
    {
        if (node == null) return double.NaN; // Number(undefined) — callers pass missing as null
        var s = JsVal.AsString(node);
        if (s != null) return JsVal.NumberCoerce(s);
        var n = JsVal.AsNumber(node);
        if (n.HasValue) return n.Value;
        if (JsVal.IsNumber(node)) return double.NaN; // non-finite
        var b = JsVal.AsBool(node);
        if (b.HasValue) return b.Value ? 1 : 0;
        return double.NaN;
    }

    // ---- posting_json_metadata parsing -----------------------------------

    private sealed class ParsedMeta
    {
        public JsonNode? Profile;
        public JsonNode? Root;
        public JsonNode? WalletsRoot;
        public JsonArray Tokens = new();
    }

    private static ParsedMeta? ParsePostingJsonMetadata(JsonNode? accountData)
    {
        if (accountData == null) return null;
        var metadataString = JsVal.AsString(JsVal.Prop(accountData, "posting_json_metadata"));
        if (string.IsNullOrEmpty(metadataString)) return null;

        JsonNode? parsed;
        try { parsed = JsonNode.Parse(metadataString!); }
        catch { return null; }

        if (parsed == null || !JsVal.IsObjectOrArray(parsed)) return null;

        var profile = JsVal.Prop(parsed, "profile") is JsonObject p ? (JsonNode?)p : null;

        var walletsRootCandidate = FirstDefined(
            JsVal.Prop(profile, "wallets"),
            JsVal.Prop(profile, "wallet"),
            JsVal.Prop(parsed, "wallets"));

        JsonArray tokensRoot;
        if (JsVal.Prop(profile, "tokens") is JsonArray pt) tokensRoot = pt;
        else if (JsVal.Prop(parsed, "tokens") is JsonArray rt) tokensRoot = rt;
        else tokensRoot = new JsonArray();

        return new ParsedMeta
        {
            Profile = profile,
            Root = parsed,
            WalletsRoot = walletsRootCandidate,
            Tokens = tokensRoot,
        };
    }

    internal static HashSet<string> ExtractEnabledEngineTokenSymbols(JsonNode? accountData)
    {
        var enabled = new HashSet<string>();
        var metadata = ParsePostingJsonMetadata(accountData);
        if (metadata == null) return enabled;

        void ConsiderToken(JsonNode? token, JsonNode? metaSource = null)
        {
            if (token == null || !JsVal.IsObjectOrArray(token)) return;

            var typeCandidate = FirstDefined(JsVal.Prop(token, "type"), JsVal.Prop(token, "layer"));
            var type = JsVal.IsString(typeCandidate) ? JsVal.AsString(typeCandidate)!.Trim().ToLowerInvariant() : "";
            if (type != "engine") return;

            var meta = metaSource is JsonObject ? metaSource
                : JsVal.Prop(token, "meta") is JsonObject tm ? tm : null;

            var showCandidate = FirstDefined(
                JsVal.IsBool(JsVal.Prop(meta, "show")) ? JsVal.Prop(meta, "show") : null,
                JsVal.IsBool(JsVal.Prop(token, "show")) ? JsVal.Prop(token, "show") : null,
                JsVal.IsBool(JsVal.Prop(token, "enabled")) ? JsVal.Prop(token, "enabled") : null);

            if (JsVal.AsBool(showCandidate) != true) return;

            var symbolCandidate = FirstDefined(
                JsVal.Prop(token, "symbol"),
                JsVal.Prop(token, "token"),
                meta != null ? (JsVal.Prop(meta, "symbol") ?? JsVal.Prop(meta, "token")) : null,
                JsVal.Prop(token, "name"));

            if (symbolCandidate == null) return;
            var normalized = JsVal.ToJsString(symbolCandidate).Trim();
            if (normalized.Length == 0) return;
            enabled.Add(normalized.ToUpperInvariant());
        }

        foreach (var token in metadata.Tokens) ConsiderToken(token);

        if (metadata.WalletsRoot is JsonObject raw)
        {
            var items = JsVal.Prop(raw, "items") as JsonArray
                        ?? JsVal.Prop(raw, "wallets") as JsonArray
                        ?? JsVal.Prop(raw, "list") as JsonArray
                        ?? new JsonArray();
            foreach (var item in items)
            {
                var meta = JsVal.Prop(item, "meta") is JsonObject m ? (JsonNode?)m : null;
                ConsiderToken(item, meta);
            }
        }

        return enabled;
    }

    internal sealed class ExternalWallet
    {
        public required string Address;
        public required string Chain;
        public required string Symbol;
        public required string Name;
        public double? Decimals;
    }

    internal static List<ExternalWallet> ExtractExternalWallets(JsonNode? accountData, bool onlyEnabled = false)
    {
        var results = new Dictionary<string, ExternalWallet>();
        var order = new List<string>();
        var metadata = ParsePostingJsonMetadata(accountData);
        if (metadata == null) return new List<ExternalWallet>();

        var profile = metadata.Profile is JsonObject ? metadata.Profile : metadata.Root;
        var walletsRoot = metadata.WalletsRoot;
        var tokensRoot = metadata.Tokens;

        void AddResult(ExternalWallet w)
        {
            var key = $"{w.Chain}::{w.Address}".ToLowerInvariant();
            if (!results.ContainsKey(key)) { results[key] = w; order.Add(key); }
        }

        if (walletsRoot is JsonObject walletObject)
        {
            var enabledFlag = JsVal.AsBool(JsVal.Prop(walletObject, "enabled"))
                ?? JsVal.AsBool(JsVal.Prop(profile, "wallets_enabled"))
                ?? true;

            if (enabledFlag)
            {
                var items = JsVal.Prop(walletObject, "items") as JsonArray
                            ?? JsVal.Prop(walletObject, "wallets") as JsonArray
                            ?? JsVal.Prop(walletObject, "list") as JsonArray
                            ?? new JsonArray();

                foreach (var rawItem in items)
                {
                    if (rawItem == null || !JsVal.IsObjectOrArray(rawItem)) continue;
                    var meta = JsVal.Prop(rawItem, "meta") is JsonObject rm ? (JsonNode?)rm : null;

                    if (onlyEnabled)
                    {
                        var showCandidate = FirstDefined(
                            JsVal.IsBool(JsVal.Prop(rawItem, "show")) ? JsVal.Prop(rawItem, "show") : null,
                            JsVal.IsBool(JsVal.Prop(rawItem, "enabled")) ? JsVal.Prop(rawItem, "enabled") : null,
                            JsVal.IsBool(JsVal.Prop(meta, "show")) ? JsVal.Prop(meta, "show") : null);
                        if (JsVal.AsBool(showCandidate) != true) continue;
                    }

                    var addressCandidate = FirstDefined(
                        JsVal.Prop(rawItem, "address"), JsVal.Prop(rawItem, "addr"),
                        JsVal.Prop(rawItem, "wallet"), JsVal.Prop(rawItem, "value"),
                        meta != null ? (JsVal.Prop(meta, "address") ?? JsVal.Prop(meta, "addr")) : null);
                    var chainCandidate = FirstDefined(
                        JsVal.Prop(rawItem, "chain"), JsVal.Prop(rawItem, "network"), JsVal.Prop(rawItem, "type"),
                        JsVal.Prop(rawItem, "token"), JsVal.Prop(rawItem, "symbol"), JsVal.Prop(rawItem, "name"));
                    var symbolCandidate = FirstDefined(
                        JsVal.Prop(rawItem, "symbol"), JsVal.Prop(rawItem, "token"), JsVal.Prop(rawItem, "name"));
                    var resolved = ResolveChainConfig(chainCandidate, symbolCandidate);

                    if (addressCandidate == null || resolved == null) continue;

                    var (chain, config) = resolved.Value;
                    var decimalsSource = FirstDefined(
                        JsVal.Prop(rawItem, "decimals"), JsVal.Prop(rawItem, "precision"),
                        JsVal.Prop(rawItem, "scale"), JsVal.Prop(rawItem, "decimal"),
                        meta != null ? (JsVal.Prop(meta, "decimals") ?? JsVal.Prop(meta, "precision")
                            ?? JsVal.Prop(meta, "scale") ?? JsVal.Prop(meta, "decimal")) : null);
                    var symbolSource = FirstDefined(
                        JsVal.Prop(rawItem, "symbol"), JsVal.Prop(rawItem, "token"),
                        JsonValue.Create(config.Symbol), JsonValue.Create(chain));
                    var nameSource = FirstDefined(
                        JsVal.Prop(rawItem, "name"), JsonValue.Create(config.Name), symbolSource);
                    var symbol = JsVal.ToJsString(symbolSource ?? JsonValue.Create(config.Symbol)).ToUpperInvariant();
                    var name = JsVal.ToJsString(nameSource ?? JsonValue.Create(symbol));
                    var decimals = ParseMaybeNumber(decimalsSource);

                    AddResult(new ExternalWallet
                    {
                        Address = JsVal.ToJsString(addressCandidate),
                        Chain = chain, Symbol = symbol, Name = name, Decimals = decimals,
                    });
                }
            }
        }

        if (tokensRoot.Count > 0)
        {
            foreach (var token in tokensRoot)
            {
                if (token == null || !JsVal.IsObjectOrArray(token)) continue;

                var typeCandidate = FirstDefined(JsVal.Prop(token, "type"), JsVal.Prop(token, "layer"));
                var type = JsVal.IsString(typeCandidate) ? JsVal.AsString(typeCandidate)!.Trim().ToLowerInvariant() : "";
                if (type != "chain") continue;

                var meta = JsVal.Prop(token, "meta") is JsonObject tm ? (JsonNode?)tm : null;

                if (onlyEnabled)
                {
                    var showCandidate = JsVal.IsBool(JsVal.Prop(meta, "show")) ? JsVal.AsBool(JsVal.Prop(meta, "show")) : null;
                    var fallbackShow = JsVal.IsBool(JsVal.Prop(token, "show")) ? JsVal.AsBool(JsVal.Prop(token, "show")) : null;
                    if (showCandidate != true && fallbackShow != true) continue;
                }

                var addressCandidate = FirstDefined(
                    meta != null ? (JsVal.Prop(meta, "address") ?? JsVal.Prop(meta, "addr")
                        ?? JsVal.Prop(meta, "wallet") ?? JsVal.Prop(meta, "value")) : null,
                    JsVal.Prop(token, "address"), JsVal.Prop(token, "wallet"), JsVal.Prop(token, "value"));

                if (addressCandidate == null) continue;

                var chainCandidate = FirstDefined(
                    JsVal.Prop(token, "chain"), JsVal.Prop(token, "network"),
                    JsVal.Prop(token, "symbol"), JsVal.Prop(token, "name"), JsVal.Prop(token, "type"));
                var symbolCandidate = FirstDefined(
                    JsVal.Prop(token, "symbol"), meta != null ? JsVal.Prop(meta, "symbol") : null, chainCandidate);
                var resolved = ResolveChainConfig(chainCandidate, symbolCandidate);
                if (resolved == null) continue;

                var (chain, config) = resolved.Value;
                var symbolSource = FirstDefined(
                    JsVal.Prop(token, "symbol"), JsonValue.Create(config.Symbol), chainCandidate, JsonValue.Create(chain));
                var nameSource = FirstDefined(
                    JsVal.Prop(token, "name"), JsonValue.Create(config.Name), symbolSource);
                var decimalsSource = FirstDefined(
                    JsVal.Prop(token, "decimals"),
                    meta != null ? (JsVal.Prop(meta, "decimals") ?? JsVal.Prop(meta, "precision")
                        ?? JsVal.Prop(meta, "scale") ?? JsVal.Prop(meta, "decimal")) : null);
                var symbol = JsVal.ToJsString(symbolSource ?? JsonValue.Create(config.Symbol)).ToUpperInvariant();
                var name = JsVal.ToJsString(nameSource ?? JsonValue.Create(symbol));
                var decimals = ParseMaybeNumber(decimalsSource);

                AddResult(new ExternalWallet
                {
                    Address = JsVal.ToJsString(addressCandidate),
                    Chain = chain, Symbol = symbol, Name = name, Decimals = decimals,
                });
            }
        }

        return order.Select(k => results[k]).ToList();
    }

    // ---- layer builders --------------------------------------------------

    internal static List<JsonObject> BuildPointsLayer(JsonNode? pointsData, JsonNode? marketData, string currency)
    {
        if (pointsData == null || !JsJson.IsTruthy(pointsData)) return new List<JsonObject>();

        var possible = new[]
        {
            JsVal.Prop(pointsData, "points"), JsVal.Prop(pointsData, "balance"),
            JsVal.Prop(pointsData, "point_balance"), JsVal.Prop(pointsData, "point"),
            JsVal.Prop(pointsData, "total_points"),
        };

        double balance = 0;
        foreach (var candidate in possible)
        {
            var parsed = ParseMaybeNumber(candidate);
            if (parsed.HasValue) { balance = parsed.Value; break; }
            if (candidate != null && JsVal.IsObjectOrArray(candidate))
            {
                var nestedSource = FirstDefined(
                    JsVal.Prop(candidate, "points"), JsVal.Prop(candidate, "balance"), JsVal.Prop(candidate, "available"));
                var nested = ParseMaybeNumber(nestedSource);
                if (nested.HasValue) { balance = nested.Value; break; }
            }
        }
        if (!double.IsFinite(balance)) balance = 0;

        var pendingCandidates = new[]
        {
            "pendingRewards", "pending_rewards", "pending", "pending_points", "pendingPoints",
            "pending_token", "pendingToken", "unclaimed", "unclaimed_points", "unclaimedPoints",
            "unclaimed_balance", "unclaimedBalance", "rewards", "claims",
        };

        double? pendingRewards = null;
        foreach (var key in pendingCandidates)
        {
            var candidate = JsVal.Prop(pointsData, key);
            if (candidate == null) continue;
            if (JsVal.IsObjectOrArray(candidate))
            {
                var nested = PickFirstNumericValue(
                    JsVal.Prop(candidate, "points"), JsVal.Prop(candidate, "balance"),
                    JsVal.Prop(candidate, "amount"), JsVal.Prop(candidate, "value"),
                    JsVal.Prop(candidate, "pending"), JsVal.Prop(candidate, "total"));
                if (nested.HasValue) { pendingRewards = nested.Value; break; }
                continue;
            }
            var parsed = ParseMaybeNumber(candidate);
            if (parsed.HasValue) { pendingRewards = parsed.Value; break; }
        }
        if (pendingRewards.HasValue && !double.IsFinite(pendingRewards.Value)) pendingRewards = null;

        var price = ConvertUsdRateToCurrency(marketData, currency, PointsUsdRate);
        var options = new ItemOptions();
        if (pendingRewards.HasValue) options.PendingRewards = pendingRewards.Value;

        return new List<JsonObject>
        {
            MakePortfolioItem("Ecency Points", "POINTS", "points", balance, price, currency, 3,
                options, Constants.AssetIconUrls["POINTS"], EcencyActions()),
        };
    }

    internal static List<JsonObject> BuildHiveLayer(JsonNode? accountData, HiveExplorer.GlobalProps? globalProps, JsonNode? marketData, string currency)
    {
        if (accountData == null || globalProps == null) return new List<JsonObject>();

        var hiveBalance = PToken(JsVal.Prop(accountData, "balance"), "0 HIVE");
        var hiveSavings = PToken(JsVal.Prop(accountData, "savings_balance"), "0 HIVE");
        var hbdBalance = PToken(JsVal.Prop(accountData, "hbd_balance"), "0 HBD");
        var hbdSavings = PToken(JsVal.Prop(accountData, "savings_hbd_balance"), "0 HBD");

        var totalVests = PToken(JsVal.Prop(accountData, "vesting_shares"), "0 VESTS");
        var delegatedVests = PToken(JsVal.Prop(accountData, "delegated_vesting_shares"), "0 VESTS");
        var receivedVests = PToken(JsVal.Prop(accountData, "received_vesting_shares"), "0 VESTS");

        var hivePerMVests = globalProps.HivePerMVests;
        double? hbdApr = double.IsFinite(globalProps.HbdApr) ? globalProps.HbdApr : null;
        double? hpApr = double.IsFinite(globalProps.HpApr) ? globalProps.HpApr : null;

        var pendingHive = PToken(JsVal.Prop(accountData, "reward_hive_balance"), "0 HIVE");
        var pendingHbd = PToken(JsVal.Prop(accountData, "reward_hbd_balance"), "0 HBD");
        var pendingVests = PToken(JsVal.Prop(accountData, "reward_vesting_balance"), "0 VESTS");
        var pendingHivePower = VestsToHivePower(pendingVests, hivePerMVests);

        var hivePrice = GetTokenPrice(marketData, "hive", currency);
        var hbdPrice = GetTokenPrice(marketData, "hbd", currency);

        var extraData = new JsonArray();
        var delegatedHP = VestsToHivePower(delegatedVests, hivePerMVests);
        var receivedHP = VestsToHivePower(receivedVests, hivePerMVests);

        var nextWithdrawalStr = JsVal.AsString(JsVal.Prop(accountData, "next_vesting_withdrawal"));
        var pwrDwnHoursLeft = HoursDifferentialFromIso(nextWithdrawalStr);
        var isPoweringDown = pwrDwnHoursLeft > 0;

        var nextVestingSharesWithdrawal = isPoweringDown
            ? Math.Min(
                PToken(JsVal.Prop(accountData, "vesting_withdraw_rate"), "0 VESTS"),
                (NumberOf(JsVal.Prop(accountData, "to_withdraw")) - NumberOf(JsVal.Prop(accountData, "withdrawn"))) / 1e6)
            : 0;
        var nextVestingSharesWithdrawalHive = isPoweringDown
            ? VestsToHivePower(nextVestingSharesWithdrawal, hivePerMVests)
            : 0;

        var availableHp = VestsToHivePower(totalVests, hivePerMVests);
        var ownedNetHp = availableHp - delegatedHP - nextVestingSharesWithdrawalHive;
        var effectiveNetHp = ownedNetHp + receivedHP;
        var availableLiquidHp = Math.Max(ownedNetHp, 0);

        if (receivedHP != 0)
            extraData.Add(new JsonObject { ["dataKey"] = "received_hive_power", ["value"] = $"+ {IntlFormat(receivedHP)} HP" });
        if (delegatedHP != 0)
            extraData.Add(new JsonObject { ["dataKey"] = "delegated_hive_power", ["value"] = $"- {IntlFormat(delegatedHP)} HP" });
        if (nextVestingSharesWithdrawalHive != 0)
            extraData.Add(new JsonObject
            {
                ["dataKey"] = "powering_down_hive_power",
                ["value"] = $"- {IntlFormat(nextVestingSharesWithdrawalHive)} HP",
                ["subValue"] = $"{JsRound(pwrDwnHoursLeft)}h",
            });
        extraData.Add(new JsonObject { ["dataKey"] = "net_hive_power", ["value"] = $"{IntlFormat(effectiveNetHp)} HP" });

        var stakedOptions = new ItemOptions { PendingRewards = pendingHivePower, Staked = availableHp };
        if (hpApr.HasValue) stakedOptions.Apr = hpApr.Value;

        var stakedHiveItem = MakePortfolioItem("Staked Hive", "HP", "hive", 0, hivePrice, currency, 3,
            stakedOptions, Constants.AssetIconUrls["HIVE"], HpActions(), extraData);
        stakedHiveItem["liquid"] = availableLiquidHp;
        stakedHiveItem["liquidFiat"] = availableLiquidHp * hivePrice;

        var hbdOptions = new ItemOptions { Savings = hbdSavings, PendingRewards = pendingHbd };
        if (hbdApr.HasValue) hbdOptions.Apr = hbdApr.Value;

        return new List<JsonObject>
        {
            stakedHiveItem,
            MakePortfolioItem("Hive", "HIVE", "hive", hiveBalance, hivePrice, currency, 3,
                new ItemOptions { Savings = hiveSavings, PendingRewards = pendingHive },
                Constants.AssetIconUrls["HIVE"], BuildHiveActions(hiveSavings)),
            MakePortfolioItem("Hive Dollar", "HBD", "hive", hbdBalance, hbdPrice, currency, 3,
                hbdOptions, Constants.AssetIconUrls["HBD"], BuildHbdActions(hbdSavings)),
        };
    }

    internal static List<JsonObject> BuildEngineLayer(JsonNode? engineData, JsonNode? marketData, string currency, double hivePrice, bool onlyEnabled = false, HashSet<string>? allowedSymbols = null)
    {
        var items = new List<JsonObject>();
        if (engineData is not JsonArray arr || arr.Count == 0) return items;

        foreach (var token in arr)
        {
            if (token == null || !JsJson.IsTruthy(token)) continue;

            var symbolStr = JsVal.AsString(JsVal.Prop(token, "symbol"));
            var nameStr = JsVal.AsString(JsVal.Prop(token, "name"));
            var rawSymbol = !string.IsNullOrEmpty(symbolStr) ? symbolStr!
                : !string.IsNullOrEmpty(nameStr) ? nameStr! : "";
            var symbolKey = rawSymbol.Length > 0 ? rawSymbol.ToUpperInvariant() : "";

            if (onlyEnabled)
            {
                if (symbolKey.Length == 0 || allowedSymbols == null || !allowedSymbols.Contains(symbolKey)) continue;
            }

            var balance = ParseMaybeNumber(JsVal.Prop(token, "balance")) ?? 0;
            var staked = ParseMaybeNumber(JsVal.Prop(token, "stakedBalance")) ?? 0;
            var ownStake = ParseMaybeNumber(JsVal.Prop(token, "stake")) ?? staked;

            var tokenPrice = JsVal.AsNumber(JsVal.Prop(token, "tokenPrice")) ?? 0;
            var priceInHive = tokenPrice > 0 ? tokenPrice : 0;
            var fiatRate = hivePrice * priceInHive;

            var symbol = !string.IsNullOrEmpty(symbolStr) ? symbolStr! : rawSymbol;
            var name = !string.IsNullOrEmpty(nameStr) ? nameStr! : symbol;
            var iconStr = JsVal.AsString(JsVal.Prop(token, "icon"));
            var iconUrl = !string.IsNullOrEmpty(iconStr) ? iconStr! : Constants.AssetIconUrls["ENGINE_PLACEHOLDER"];
            var precision = (int)(JsVal.AsNumber(JsVal.Prop(token, "precision")) ?? 0);

            var pendingRewards = JsVal.AsNumber(JsVal.Prop(token, "pendingRewards"));

            var itemOptions = new ItemOptions { Staked = staked };
            if (pendingRewards.HasValue) itemOptions.PendingRewards = pendingRewards.Value;

            var actions = new JsonArray { new JsonObject { ["id"] = "transfer" } };
            var stakingEnabled = JsVal.AsBool(JsVal.Prop(token, "stakingEnabled")) ?? false;
            if (stakingEnabled)
            {
                actions.Add(new JsonObject { ["id"] = "stake" });
                if (ownStake > 0) actions.Add(new JsonObject { ["id"] = "unstake" });
            }
            if (JsJson.IsTruthy(JsVal.Prop(token, "delegationEnabled")))
            {
                actions.Add(new JsonObject { ["id"] = "delegate" });
                actions.Add(new JsonObject { ["id"] = "undelegate" });
            }

            var delegationsInNode = JsVal.Prop(token, "delegationsIn");
            var delegationsOutNode = JsVal.Prop(token, "delegationsOut");
            var extraData = new JsonArray
            {
                new JsonObject { ["dataKey"] = "delegations_in", ["value"] = NotZeroStr(delegationsInNode) },
                new JsonObject { ["dataKey"] = "delegations_out", ["value"] = NotZeroStr(delegationsOutNode) },
            };

            items.Add(MakePortfolioItem(
                !string.IsNullOrEmpty(name) ? name : symbol,
                !string.IsNullOrEmpty(symbol) ? symbol : (!string.IsNullOrEmpty(name) ? name : "ENGINE"),
                "engine", balance, fiatRate, currency, precision, itemOptions, iconUrl, actions, extraData));
        }

        return items;
    }

    // `${x}` when x !== 0, else '0.00'  (JS: 0 !== 0 is false so numeric 0 -> '0.00')
    private static string NotZeroStr(JsonNode? node)
    {
        var num = JsVal.AsNumber(node);
        if (num.HasValue) return num.Value != 0 ? JsVal.JsNumberToString(num.Value) : "0.00";
        // non-number: `${value}` template coercion; `!== 0` is true for non-numbers
        return JsVal.ToJsString(node);
    }

    internal static List<JsonObject> BuildSpkLayer(JsonNode? spkData, JsonNode? marketData, string currency)
    {
        var items = new List<JsonObject>();
        if (spkData == null || !JsVal.IsObjectOrArray(spkData)) return items;

        var balanceSource = JsVal.Prop(spkData, "balance") is JsonObject b ? (JsonNode)b
            : JsVal.Prop(spkData, "balances") is JsonObject b2 ? b2
            : new JsonObject();

        double? ReadValue(params string[] keys)
        {
            var candidates = new List<JsonNode?>();
            foreach (var key in keys)
            {
                if (JsVal.HasProp(balanceSource, key)) candidates.Add(JsVal.Prop(balanceSource, key));
                if (JsVal.HasProp(spkData, key)) candidates.Add(JsVal.Prop(spkData, key));
                if (JsVal.Prop(spkData, "account") is JsonObject acc && JsVal.HasProp(acc, key)) candidates.Add(JsVal.Prop(acc, key));
                if (JsVal.Prop(spkData, "power") is JsonObject pow && JsVal.HasProp(pow, key)) candidates.Add(JsVal.Prop(pow, key));
            }
            return PickFirstNumericValue(candidates.ToArray());
        }

        var spkBalance = ReadValue("spk", "SPK", "balance_spk", "liquid_spk");
        if (spkBalance.HasValue)
        {
            var spkPrice = GetTokenPrice(marketData, "spk", currency);
            items.Add(MakePortfolioItem("SPK", "SPK", "spk", spkBalance.Value, spkPrice, currency, 3,
                new ItemOptions(), Constants.AssetIconUrls["SPK"], SpkActions()));
        }

        var larynxBalance = ReadValue("larynx", "LARYNX");
        var larynxPower = ReadValue("larynx_power", "larynxPower", "LARYNX_POWER");

        // larynx power breakdown — JS: spkData.poweredUp / 1000, granted?.t / 1000, ...
        var poweredUp = JsVal.AsNumber(JsVal.Prop(spkData, "poweredUp"));
        var larPower = (poweredUp ?? double.NaN) / 1000; // undefined/1000 -> NaN
        var grantedT = JsVal.AsNumber(JsVal.Prop(JsVal.Prop(spkData, "granted"), "t"));
        var grantedPwr = grantedT is > 0 or < 0 ? grantedT!.Value / 1000 : 0; // granted?.t ? .../1000 : 0
        var grantingT = JsVal.AsNumber(JsVal.Prop(JsVal.Prop(spkData, "granting"), "t"));
        var grantingPwr = grantingT is > 0 or < 0 ? grantingT!.Value / 1000 : 0;
        var netSpkPower = larPower + grantedPwr + grantingPwr;

        var extraData = new JsonArray();
        var powerDowns = JsVal.Prop(spkData, "power_downs");
        if (JsJson.IsTruthy(powerDowns))
        {
            var count = powerDowns is JsonObject pdo ? pdo.Count : 0;
            extraData.Add(new JsonObject { ["dataKey"] = "scheduled_power_downs", ["value"] = count.ToString() });
        }
        extraData.Add(new JsonObject { ["dataKey"] = "delegated_larynx_power", ["value"] = $"{ToFixed(grantedPwr, 3)} LP" });
        extraData.Add(new JsonObject { ["dataKey"] = "delegating_larynx_power", ["value"] = $"- {ToFixed(grantingPwr, 3)} LP" });
        extraData.Add(new JsonObject { ["dataKey"] = "total_larynx_power", ["value"] = $"{ToFixed(netSpkPower, 3)} LP" });

        if (larynxBalance.HasValue || larynxPower.HasValue)
        {
            var liquid = larynxBalance ?? 0;
            var staked = larynxPower ?? 0;
            var larynxPrice = GetTokenPrice(marketData, "larynx", currency);
            items.Add(MakePortfolioItem("LARYNX", "LARYNX", "spk", liquid, larynxPrice, currency, 3,
                new ItemOptions { Staked = staked }, Constants.AssetIconUrls["SPK_PLACEHOLDER"], LarynxActions(), extraData));
        }

        return items;
    }

    internal static async Task<List<R>> ProcessWithConcurrencyLimit<T, R>(IReadOnlyList<T> items, Func<T, Task<R>> processor, int concurrencyLimit)
    {
        var results = new R[items.Count];
        using var gate = new SemaphoreSlim(concurrencyLimit);
        var tasks = new List<Task>();
        for (var i = 0; i < items.Count; i++)
        {
            var index = i;
            await gate.WaitAsync();
            tasks.Add(Task.Run(async () =>
            {
                try { results[index] = await processor(items[index]); }
                finally { gate.Release(); }
            }));
        }
        await Task.WhenAll(tasks);
        return results.ToList();
    }

    // Per-wallet cap on the balance fetch. The chain providers allow 10s per
    // attempt, but the portfolioV2 chain leg has a 4.5s budget for the WHOLE
    // layer — without a per-wallet cap, one cold/slow provider blows the leg
    // timeout and the entire chain layer silently disappears from the response
    // (observed intermittently in production). 2s keeps two concurrency
    // batches inside the leg budget; the capped wallet degrades to an error
    // item instead of sinking its siblings.
    private const int ChainBalanceTimeoutMs = 2000;

    internal static async Task<List<JsonObject>> BuildChainLayer(JsonNode? accountData, JsonNode? marketData, string currency, bool? onlyEnabled)
    {
        var wallets = ExtractExternalWallets(accountData, onlyEnabled == true);
        if (wallets.Count == 0) return new List<JsonObject>();

        var items = await ProcessWithConcurrencyLimit(wallets, async wallet =>
        {
            var chain = wallet.Chain.ToLowerInvariant();
            var config = ChainConfigs[chain];
            var decimals = (int)(wallet.Decimals ?? config.Decimals);

            try
            {
                var fetchTask = PrivateApi.FetchChainBalance(chain, wallet.Address);
                var completed = await Task.WhenAny(fetchTask, Task.Delay(ChainBalanceTimeoutMs));
                if (completed != fetchTask) throw new Exception("Chain balance request timed out");
                var data = await fetchTask;
                var balance = ConvertChainBalanceToAmount(data, decimals);
                var price = GetTokenPrice(marketData, wallet.Symbol, currency);
                var iconUrl = config.IconUrl ?? Constants.AssetIconUrls["CHAIN_PLACEHOLDER"];

                return MakePortfolioItem(
                    !string.IsNullOrEmpty(wallet.Name) ? wallet.Name : config.Name,
                    !string.IsNullOrEmpty(wallet.Symbol) ? wallet.Symbol : config.Symbol,
                    "chain", balance, price, currency, decimals,
                    new ItemOptions { Address = wallet.Address }, iconUrl, ChainActions());
            }
            catch (Exception err)
            {
                var errorMessage = err.Message ?? "Chain balance request failed";
                Console.WriteLine($"Failed to fetch external wallet balance {chain} {wallet.Address}");
                var price = GetTokenPrice(marketData, wallet.Symbol, currency);
                return MakePortfolioItem(
                    !string.IsNullOrEmpty(wallet.Name) ? wallet.Name : config.Name,
                    !string.IsNullOrEmpty(wallet.Symbol) ? wallet.Symbol : config.Symbol,
                    "chain", 0, price, currency, decimals,
                    new ItemOptions { Address = wallet.Address, Error = errorMessage },
                    config.IconUrl ?? Constants.AssetIconUrls["CHAIN_PLACEHOLDER"], ChainActions());
            }
        }, 3);

        return items.Where(x => x != null).ToList();
    }

    // ---- JS numeric string formatting ------------------------------------

    // Intl.NumberFormat().format(x): en-US grouping, up to 3 fraction digits.
    private static string IntlFormat(double x)
    {
        if (double.IsNaN(x)) return "NaN";
        if (double.IsInfinity(x)) return x > 0 ? "∞" : "-∞";
        var nfi = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
        nfi.NumberGroupSeparator = ",";
        nfi.NumberDecimalSeparator = ".";
        var rounded = Math.Round(x, 3, MidpointRounding.AwayFromZero);
        return rounded.ToString("#,##0.###", nfi);
    }

    // Number.prototype.toFixed(digits)
    private static string ToFixed(double x, int digits)
    {
        if (double.IsNaN(x)) return "NaN";
        return x.ToString("F" + digits, CultureInfo.InvariantCulture);
    }

    // Math.round: half rounds toward +Infinity
    private static long JsRound(double x) => (long)Math.Floor(x + 0.5);

    // getHoursDifferntial(new Date(`${iso}.000Z`), new Date()): hours until iso.
    private static double HoursDifferentialFromIso(string? iso)
    {
        if (iso == null) return double.NaN; // new Date("undefined.000Z") -> Invalid -> NaN
        if (!DateTimeOffset.TryParse(iso + ".000Z", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var target))
            return double.NaN;
        var now = DateTimeOffset.UtcNow;
        return (target.ToUnixTimeMilliseconds() - now.ToUnixTimeMilliseconds()) / (60.0 * 60.0 * 1000.0);
    }
}
