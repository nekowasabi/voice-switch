"""Jev questions for the journal classifier. All wording lives here (docs/journal-triage-design.md).

Examples in criteria are invented sentences, never the user's own utterances: those stay held out for review.
"""

QUESTIONS = {
    "is_filler": {
        "type": "noul",
        "instructions": "Is `utterance.text` only a sound with no meaningful content?",
        "criteria": {
            "true": "A backchannel, hesitation, cut-off restart, misrecognized noise, or a short call to someone in the room, "
                    "such as 「うん」「えーと、あれ」「はいはい」.",
            "false": "It carries a thought, observation, plan, wish, question, or instruction, even if short or cut off, "
                     "such as 「駅の傘立て、いつも満杯だな」「明日の会議を 10 時にずらして」.",
        },
    },
    "wants_action": {
        "type": "noul",
        "instructions": "Does the speaker of `utterance.text` want something to be done, beyond keeping the thought?",
        "criteria": {
            "true": "The speaker wants themselves, a computer, or an AI to do something, such as 「牛乳を買っておかないと」「この表を週ごとにまとめて」.",
            "false": "The speaker only states an observation, feeling, or idea, such as 「この坂、思ったより長いな」.",
        },
    },
    "kind_self_task": {
        "type": "noul",
        "instructions": "In `utterance.text`, read as a continuation of `previous` when present, does the speaker name a chore or task they will do themselves?",
        "criteria": {"true": "Such as 「牛乳を買っておかないと」.",
                     "false": "The work is for someone else or a computer, or there is no task."},
    },
    "kind_look_up": {
        "type": "noul",
        "instructions": "In `utterance.text`, read as a continuation of `previous` when present, does the speaker themselves want to learn an answer now?",
        "criteria": {"true": "The speaker wants to know something, such as 「この電池の寿命はどれくらいなんだろう」「来週の天気を調べて」.",
                     "false": "There is no question the speaker wants answered now. Asking to give a tool or bot the ability to look things up "
                              "is a change to that tool, such as 「このアプリで天気を調べられるようにして」."},
    },
    "kind_write_text": {
        "type": "noul",
        "instructions": "In `utterance.text`, read as a continuation of `previous` when present, does the speaker want a message or document drafted?",
        "criteria": {"true": "Such as 「大家さんへのお礼のメールを書かなきゃ」.",
                     "false": "No text needs to be written."},
    },
    "kind_computer_change": {
        "type": "noul",
        "instructions": "In `utterance.text`, read as a continuation of `previous` when present, does the speaker want a computer or AI agent to change a setting, code, or file?",
        "criteria": {"true": "Including giving a tool or bot a new ability, such as 「このスクリプトで祝日も数えられるようにして」「このアプリで天気を調べられるようにして」.",
                     "false": "Nothing on a computer needs to change."},
    },
}

# Alternative wordings compared side by side in the probe only. Empty: the two tried on 2026-11-21 did no better.
VARIANTS = {}
