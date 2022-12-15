/*
  id - incremental
  title - title of announcement
  description - description of announcement
  button_text - text of actionable button
  button_link - link that actionable button opens
  path - which path it should show, supports regex on location
*/

export const announcements = [
    {
      "id": 101,
      "title": "Happy Ho ho holidays!",
      "description": "Are you participating in our annual Advent Calendar to celebrate holidays and earn more?",
      "button_text": "Check it out",
      "button_link": "https://ecency.com/created/adventcalendar",
      "path": "/(hot|created|trending|rising|controversial)"
    },
    {
      "id": 102,
      "title": "Support Ecency",
      "description": "You can support Ecency team by voting on our proposal.",
      "button_text": "Learn More",
      "button_link": "https://ecency.com/proposals/245",
      "path": "/@[\w\.\d-]+/(posts/wallet|points|engine|permissions|spk)"
    }
]
  