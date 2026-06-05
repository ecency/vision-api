/*
  Feature Spotlight content - single source of truth for the "did you know?" cards.
  Edit this array (set start/end or bump weight) to rotate the spotlight; vision-api's
  own CI deploys it. Mirrors announcements.ts. The server filters by the start/end
  window only; auth/path/dismiss are applied client-side (web + mobile).
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
  auth        - require logged-in user (default true); set false to also show to anon
  platforms   - which platform(s) may show it: "web" (website) and/or "mobile" (mobile app). omit = both
  start       - ISO date (UTC); do not show before this. Bare "YYYY-MM-DD" = that day at 00:00Z
  end         - ISO date (UTC); inclusive - bare "YYYY-MM-DD" shows through the END of that day
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
    auth?: boolean;
    platforms?: ("web" | "mobile")[];
    start?: string;
    end?: string;
    weight?: number;
    locales?: { [lang: string]: Pick<Spotlight, "title" | "description" | "button_text"> };
}

export const spotlights: Spotlight[] = [
    // Guest signup prompt for anonymous visitors. NOTE: auth:false also shows to logged-in
    // users, so this is kept at weight 1. As long as a higher-weight auth:true card is always
    // active (the schedule below keeps continuous coverage), logged-in users see the
    // educational card and only signed-out visitors see this one. Keep coverage continuous,
    // or remove this card, otherwise logged-in users will see "Sign up" during any gap.
    {
        id: "signup-2026-summer",
        feature: "signup",
        title: "New to Ecency?",
        description: "Join in seconds and start earning Points for everything you post and do.",
        button_text: "Create account",
        button_link: "/signup",
        auth: false,
        start: "2026-06-05",
        end: "2026-08-27",
        weight: 1
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
        auth: true,
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
        auth: true,
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
        auth: true,
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
        auth: true,
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
        auth: true,
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
        auth: true,
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
        auth: true,
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
        auth: true,
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
        auth: true,
        start: "2026-08-14",
        end: "2026-08-27",
        weight: 10
    }
];
