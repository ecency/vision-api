/*
  id - incremental
  title - title of announcement
  description - description of announcement
  button_text - text of actionable button
  button_link - link that actionable button opens
  path - which path it should show, supports regex on location
  auth - should there be authorized/logged in user to show announcement,
  ops - hive uri format operation for mobile app signing
*/

export const announcements = [
    /*{
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
    },
    {
      "id": 103,
      "title": "Hi! 👋 I am new announcer",
      "description": "Occasionally, when there is really important announcements, I will show up and inform you. Hope you don't mind! 😉",
      "button_text": "Okay",
      "button_link": "/",
      "path": "/",
      "auth": true
    },
    {
      "id": 104,
      "title": "Introducing Waves and Decks 2.0 🎉",
      "description": "Short-content publishing, surfing is here! Next iteration of Social Dashboard will revolutionize your experience on Web3.0. ",
      "button_text": "Okay",
      "button_link": "/decks",
      "path": "/(hot|created|trending|rising|controversial)",
      "auth": true
    },
    {
        "id": 105,
        "title": "Support Ecency! ❤️",
        "description": "You can support Ecency team by voting on New Ecency proposal. Every vote and support counts!",
        "button_text": "Support now",
        "button_link": "/proposals/283",
        "path": "/@.+/(blog|posts|wallet|points|engine|permissions|spk)",
        "auth": true
    },*/
    {
        "id": 106,
        "title": "Vote New Proposal! ❤️",
        "description": "We still need your support. Every vote and support matters!",
        "button_text": "Support now",
        "button_link": "/proposals/283",
        "path": "/@.+/(blog|posts|wallet|points|engine|permissions|spk)",
        "auth": true,
        "ops": "hive://sign/op/WyJ1cGRhdGVfcHJvcG9zYWxfdm90ZXMiLHsidm90ZXIiOiJfX3NpZ25lciIsInByb3Bvc2FsX2lkcyI6IjI4MyIsImFwcHJvdmUiOnRydWUsImV4dGVuc2lvbnMiOltdfV0."
    }
]
