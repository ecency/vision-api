/*
  Feature Spotlight content — single source of truth for the "did you know?" cards.
  Edit this array (set start/end or bump weight) to rotate the spotlight; vision-api's
  own CI deploys it. Mirrors announcements.ts. The server filters by the start/end
  window only; auth/path/dismiss are applied client-side (web + mobile).
  Keep this Spotlight interface in sync with @ecency/sdk (types/spotlight.ts) — clients
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
  start       - ISO date (UTC); do not show before this. Bare "YYYY-MM-DD" = that day at 00:00Z
  end         - ISO date (UTC); inclusive — bare "YYYY-MM-DD" shows through the END of that day
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
    start?: string;
    end?: string;
    weight?: number;
    locales?: { [lang: string]: Pick<Spotlight, "title" | "description" | "button_text"> };
}

export const spotlights: Spotlight[] = [
    {
        id: "waves-2026-w23",
        feature: "waves",
        title: "Have you tried Waves?",
        description: "Share short posts and join the conversation on Hive in seconds.",
        icon: "wave",
        button_text: "Open Waves",
        button_link: "/waves",
        auth: true,
        start: "2026-06-01",
        end: "2026-06-30",
        weight: 10
    }
];
