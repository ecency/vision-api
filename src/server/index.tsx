import express from "express";

import fallbackHandler, {healthCheck} from "./handlers/fallback";

import * as privateApi from "./handlers/private-api";
import * as searchApi from "./handlers/search-api";
import * as authApi from "./handlers/auth-api";
import * as engineApi from "./handlers/engine-api";

const cors = require("cors")

const server = express();

server
    .disable("x-powered-by")
    .use(express.static(process.env.RAZZLE_PUBLIC_DIR!))
    .use(express.json())
    .use(cors())
    .use(express.json({limit: '50mb'}))
    .use(express.urlencoded({limit: '50mb'}))

    // Search Api
    .post("^/search-api/search$", searchApi.search)
    .post("^/search-api/search-follower$", searchApi.searchFollower)
    .post("^/search-api/search-following$", searchApi.searchFollowing)
    .post("^/search-api/search-account$", searchApi.searchAccount)
    .post("^/search-api/search-tag$", searchApi.searchTag)
    .post("^/search-api/search-path$", searchApi.searchPath)

    // Auth Api
    .post("^/auth-api/hs-token-refresh$", authApi.hsTokenRefresh)

    // Engine Api
    .post("^/private-api/engine-api$", engineApi.eapi)
    .get("^/private-api/engine-reward-api/:username$", engineApi.erewardapi)
    .get("^/private-api/engine-chart-api$", engineApi.echartapi)
    .get("^/private-api/engine-account-history", engineApi.engineAccountHistory)

    // Private Api
    .get("^/private-api/received-vesting/:username$", privateApi.receivedVesting)
    .get("^/private-api/received-rc/:username$", privateApi.receivedRC)
    .get("^/private-api/rewarded-communities$", privateApi.rewardedCommunities)
    .get("^/private-api/leaderboard/:duration(day|week|month)$", privateApi.leaderboard)
    .get("^/private-api/curation/:duration(day|week|month)$", privateApi.curation)
    .get("^/private-api/promoted-entries$", privateApi.promotedEntries)
    .get("^/private-api/market-data/:fiat/:token$", privateApi.marketData)
    .get("^/private-api/market-data/latest$", privateApi.marketDataLatest)
    .get("^/private-api/referrals/:username$", privateApi.referrals)
    .get("^/private-api/referrals/:username/stats$", privateApi.referralsStats)
    .get("^/private-api/announcements$", privateApi.getAnnouncement)
    .get("^/private-api/chats-pub/:username$", privateApi.chatsPub)
    .get("^/private-api/channel/:username$", privateApi.channelGet)

    .post("^/private-api/comment-history$", privateApi.commentHistory)
    .post("^/private-api/points$", privateApi.points)
    .post("^/private-api/point-list$", privateApi.pointList)
    .post("^/private-api/account-create$", privateApi.createAccount)
    .post("^/private-api/account-create-friend$", privateApi.createAccountFriend)
    .post("^/private-api/subscribe$", privateApi.subscribeNewsletter)
    .post("^/private-api/notifications$", privateApi.notifications)
    .post("^/private-api/report$", privateApi.report)
    .post("^/private-api/request-delete$", privateApi.report)
    .post("^/private-api/post-reblogs$", privateApi.reblogs)
    .post("^/private-api/post-reblog-count$", privateApi.reblogCount)
    .post("^/private-api/portfolio$", privateApi.portfolio)


    /* Login required private api endpoints */
    .post("^/private-api/notifications/unread$", privateApi.unreadNotifications)
    .post("^/private-api/notifications/mark$", privateApi.markNotifications)
    .post("^/private-api/register-device$", privateApi.registerDevice)
    .post("^/private-api/detail-device$", privateApi.detailDevice)
    .post("^/private-api/images$", privateApi.images)
    .post("^/private-api/images-delete$", privateApi.imagesDelete)
    .post("^/private-api/images-add$", privateApi.imagesAdd)
    .post("^/private-api/drafts$", privateApi.drafts)
    .post("^/private-api/drafts-add$", privateApi.draftsAdd)
    .post("^/private-api/drafts-update$", privateApi.draftsUpdate)
    .post("^/private-api/drafts-delete$", privateApi.draftsDelete)
    .post("^/private-api/bookmarks$", privateApi.bookmarks)
    .post("^/private-api/bookmarks-add$", privateApi.bookmarksAdd)
    .post("^/private-api/bookmarks-delete$", privateApi.bookmarksDelete)
    .post("^/private-api/schedules$", privateApi.schedules)
    .post("^/private-api/schedules-add$", privateApi.schedulesAdd)
    .post("^/private-api/schedules-delete$", privateApi.schedulesDelete)
    .post("^/private-api/schedules-move$", privateApi.schedulesMove)
    .post("^/private-api/favorites$", privateApi.favorites)
    .post("^/private-api/favorites-check$", privateApi.favoritesCheck)
    .post("^/private-api/favorites-add$", privateApi.favoritesAdd)
    .post("^/private-api/favorites-delete$", privateApi.favoritesDelete)
    .post("^/private-api/fragments$", privateApi.fragments)
    .post("^/private-api/fragments-add$", privateApi.fragmentsAdd)
    .post("^/private-api/fragments-update$", privateApi.fragmentsUpdate)
    .post("^/private-api/fragments-delete$", privateApi.fragmentsDelete)
    .post("^/private-api/decks$", privateApi.decks)
    .post("^/private-api/decks-add$", privateApi.decksAdd)
    .post("^/private-api/decks-update$", privateApi.decksUpdate)
    .post("^/private-api/decks-delete$", privateApi.decksDelete)
    .post("^/private-api/recoveries$", privateApi.recoveries)
    .post("^/private-api/recoveries-add$", privateApi.recoveriesAdd)
    .post("^/private-api/recoveries-update$", privateApi.recoveriesUpdate)
    .post("^/private-api/recoveries-delete$", privateApi.recoveriesDelete)
    .post("^/private-api/points-claim$", privateApi.pointsClaim)
    .post("^/private-api/points-calc$", privateApi.pointsCalc)
    .post("^/private-api/promote-price$", privateApi.promotePrice)
    .post("^/private-api/promoted-post$", privateApi.promotedPost)
    .post("^/private-api/boost-options$", privateApi.boostOptions)
    .post("^/private-api/boosted-post$", privateApi.boostedPost)
    .post("^/private-api/usr-activity$", privateApi.activities)
    .post("^/private-api/get-game$", privateApi.gameGet)
    .post("^/private-api/post-game$", privateApi.gamePost)
    .post("^/private-api/purchase-order$", privateApi.purchaseOrder)
    .post("^/private-api/chats$", privateApi.chats)
    .post("^/private-api/chats-add$", privateApi.chatsAdd)
    .post("^/private-api/chats-update$", privateApi.chatsUpdate)
    .post("^/private-api/channel-add$", privateApi.channelAdd)


    // Health check script for docker swarm
    .get("^/healthcheck.json$", healthCheck)

    // For all others paths
    .get("*", fallbackHandler);


export default server;
