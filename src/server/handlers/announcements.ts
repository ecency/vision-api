/*
  id - incremental
  title - title of announcement
  description - description of announcement
  button_text - text of actionable button
  button_link - link that actionable button opens
  path - which path it should show, supports regex on location
  auth - should there be authorized/logged in user to show announcement
*/

export const announcements = [
    {
      "id": 101,
      "title": "Happy Ho ho ho holidays! 🎉",
      "description": "Are you participating in our annual Advent Calendar to celebrate holidays and earn more?",
      "button_text": "Check it out",
      "button_link": "/created/adventcalendar",
      "path": "/(hot|created|trending|rising|controversial)",
      "auth": true
    },
    {
      "id": 102,
      "title": "Support Ecency! ❤️",
      "description": "You can support Ecency team by voting on Ecency proposal. Every vote and support counts!",
      "button_text": "Support now",
      "button_link": "/proposals/245",
      "path": "/@.+/(blog|posts|wallet|points|engine|permissions|spk)",
      "auth": true
    }
]
  