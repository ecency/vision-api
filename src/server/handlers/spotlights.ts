/*
  Feature Spotlight content - single source of truth for the "did you know?" cards.
  Edit this array (set start/end or bump weight) to rotate the spotlight; vision-api's
  own CI deploys it. Mirrors announcements.ts. The server filters by the start/end
  window only; guestsOnly/path/dismiss are applied client-side (web + mobile).
  Keep this Spotlight interface in sync with @ecency/sdk (types/spotlight.ts) - clients
  parse exactly this wire shape.

  id          - stable slug, e.g. "waves-2026-w23"; used as the dismiss key, never reuse
  feature     - which feature this promotes (waves|polls|points|communities|...), for gating
  title       - card title
  description - card body
  icon        - optional icon name or i.ecency.com image url
  button_text - actionable button text
  button_link - internal route ("/waves") or external url the button opens
  path        - which path(s) it should show on, supports regex on location; omit = everywhere
  guestsOnly  - show only to signed-out visitors; omit = logged-in users only
  platforms   - which platform(s) may show it: "web" (website) and/or "mobile" (mobile app). omit = both
  start       - ISO date (UTC); do not show before this. Bare "YYYY-MM-DD" = that day at 00:00Z
  end         - ISO date (UTC); inclusive - bare "YYYY-MM-DD" shows through the END of that day. Omit = never expires
  weight      - higher wins when multiple are active
  locales     - optional per-language overrides for title/description/button_text
*/

export interface Spotlight {
    id: string;
    feature: string;
    title: string;
    description: string;
    icon?: string;
    button_text: string;
    button_link: string;
    path?: string | string[];
    guestsOnly?: boolean;
    platforms?: ("web" | "mobile")[];
    start?: string;
    end?: string;
    weight?: number;
    locales?: { [lang: string]: Pick<Spotlight, "title" | "description" | "button_text"> };
}

export const spotlights: Spotlight[] = [
    // Guest funnel: shown only to signed-out visitors (guestsOnly). These never expire and
    // descend in weight, so a guest sees the top hook first and the next one each time they
    // dismiss. Logged-in users never match a guestsOnly card, so there is no overlap with the
    // educational cards below.
    {
        id: "guest-signup",
        feature: "signup",
        title: "New to Ecency?",
        description: "Create your free account and start earning rewards for everything you post.",
        button_text: "Create account",
        button_link: "/signup",
        guestsOnly: true,
        start: "2026-06-05",
        weight: 10
    },
    {
        id: "guest-earn",
        feature: "signup",
        title: "Get paid to post",
        description: "On Ecency the rewards go to creators, not the platform. Your posts, your earnings.",
        button_text: "Join free",
        button_link: "/signup",
        guestsOnly: true,
        start: "2026-06-05",
        weight: 9
    },
    {
        id: "guest-own",
        feature: "signup",
        title: "Own your content",
        description: "No ads, no gatekeepers. Your account lives on the blockchain, and it is yours.",
        button_text: "Get started",
        button_link: "/signup",
        guestsOnly: true,
        start: "2026-06-05",
        weight: 8
    },

    // ---- Jun 5 - 18 ----
    {
        id: "web-waves-2026-06",
        feature: "waves",
        title: "Have you tried Waves?",
        description: "Share short posts and jump into the conversation on Hive in seconds.",
        button_text: "Open Waves",
        button_link: "/waves",
        platforms: ["web"],
        start: "2026-06-05",
        end: "2026-06-18",
        weight: 10
    },
    {
        id: "mob-trending-2026-06",
        feature: "discover",
        title: "See what's trending",
        description: "Tap into the most popular posts on Hive right now.",
        button_text: "View trending",
        button_link: "/trending",
        platforms: ["mobile"],
        start: "2026-06-05",
        end: "2026-06-18",
        weight: 10
    },

    // ---- Jun 19 - Jul 2 ----
    {
        id: "communities-2026-06",
        feature: "communities",
        title: "Find your community",
        description: "Join topic-based communities and meet people who share your interests.",
        button_text: "Browse communities",
        button_link: "/communities",
        start: "2026-06-19",
        end: "2026-07-02",
        weight: 10
    },

    // ---- Jul 3 - 16 ----
    {
        id: "web-decks-2026-07",
        feature: "decks",
        title: "Power up with Decks",
        description: "A customizable multi-column dashboard. Track tags, communities, and notifications side by side.",
        button_text: "Open Decks",
        button_link: "/decks",
        platforms: ["web"],
        start: "2026-07-03",
        end: "2026-07-16",
        weight: 10
    },
    {
        id: "mob-hot-2026-07",
        feature: "discover",
        title: "Catch what's hot",
        description: "See the posts heating up across Hive.",
        button_text: "View hot",
        button_link: "/hot",
        platforms: ["mobile"],
        start: "2026-07-03",
        end: "2026-07-16",
        weight: 10
    },

    // ---- Jul 17 - 30 ----
    {
        id: "wallet-2026-07",
        feature: "wallet",
        title: "Your wallet, at a glance",
        description: "Check your HIVE, HBD, Hive Power and Points, then claim your rewards.",
        button_text: "Open wallet",
        button_link: "/wallet",
        start: "2026-07-17",
        end: "2026-07-30",
        weight: 10
    },

    // ---- Jul 31 - Aug 13 ----
    {
        id: "search-2026-08",
        feature: "search",
        title: "Find people and topics to follow",
        description: "Search posts, tags, and creators across Hive, and follow the ones you love.",
        button_text: "Start searching",
        button_link: "/search",
        start: "2026-07-31",
        end: "2026-08-13",
        weight: 10
    },

    // ---- Aug 14 - 27 ----
    {
        id: "web-points-2026-08",
        feature: "points",
        title: "Earn Ecency Points",
        description: "Get rewarded for posting and engaging, then spend Points to boost your reach.",
        button_text: "Explore perks",
        button_link: "/perks",
        platforms: ["web"],
        start: "2026-08-14",
        end: "2026-08-27",
        weight: 10
    },
    {
        id: "mob-bookmarks-2026-08",
        feature: "bookmarks",
        title: "Save it for later",
        description: "Bookmark posts you want to come back to, they are always one tap away.",
        button_text: "Open bookmarks",
        button_link: "/bookmarks",
        platforms: ["mobile"],
        start: "2026-08-14",
        end: "2026-08-27",
        weight: 10
    }
];
