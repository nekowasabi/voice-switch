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
        "instructions": "In `utterance.text`, does the speaker name a chore or task they will do themselves?",
        "criteria": {"true": "Such as 「牛乳を買っておかないと」.",
                     "false": "The work is for someone else or a computer, or there is no task."},
    },
    "kind_look_up": {
        "type": "noul",
        "instructions": "In `utterance.text`, does the speaker want to find out an answer to something?",
        "criteria": {"true": "Such as 「この電池の寿命はどれくらいなんだろう」.",
                     "false": "There is no question the speaker wants answered."},
    },
    "kind_write_text": {
        "type": "noul",
        "instructions": "In `utterance.text`, does the speaker want a message or document drafted?",
        "criteria": {"true": "Such as 「大家さんへのお礼のメールを書かなきゃ」.",
                     "false": "No text needs to be written."},
    },
    "kind_computer_change": {
        "type": "noul",
        "instructions": "In `utterance.text`, does the speaker want a computer or AI agent to change a setting, code, or file?",
        "criteria": {"true": "Such as 「このスクリプトで祝日も数えられるようにして」.",
                     "false": "Nothing on a computer needs to change."},
    },
}

# Alternative wordings compared side by side in the probe only. Each is answered on its own, over the same state.
VARIANTS = {
    "has_content": {
        "type": "noul",
        "instructions": "Does `utterance.text` contain something the speaker might want to read again later?",
        "criteria": {
            "true": "A thought, observation, plan, wish, question, or instruction, even if half-formed.",
            "false": "Only a backchannel, hesitation, cut-off restart, or misrecognized noise, such as 「うん」「えーと」.",
        },
    },
    "wants_action_ctx": {
        "type": "noul",
        "instructions": "Given `context` and `previous`, does the speaker of `utterance.text` want something to be done?",
        "criteria": {
            "true": "The speaker wants themselves, a computer, or an AI to do something.",
            "false": "The speaker only states an observation, feeling, or idea.",
        },
    },
}
