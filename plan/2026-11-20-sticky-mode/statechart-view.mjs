export const title = "スティッキーモード";

export const colors = {
  off: "tan",
  hold: "blue",
  send: "orange",
  done: "teal",
  back: "yellow",
};

export const group = (v) => {
  if (!v.on && v.phase === "idle") return "off";
  if (v.phase === "delivered") return "done";
  if (v.phase === "fellback") return "back";
  if (v.phase === "delivering") return "send";
  return "hold";
};

export const state = (v) => {
  if (!v.on && v.phase === "idle") return "オフ";
  if (v.phase === "idle") return "待機";
  if (v.phase === "armed") return v.alive ? "覚えた" : "覚えたが宛先が消えた";
  if (v.phase === "recording") {
    if (v.kind === "secure") return "パスワード欄";
    if (v.kind === "nofield") return "入力欄がない";
    if (!v.alive) return "宛先が消えた";
    return v.switched ? "録音中 / 別のアプリ" : "録音中";
  }
  if (v.phase === "delivering") {
    if (!v.alive) return v.deliveries === 1 ? "貼ったあと宛先が消えた" : "貼る前に宛先が消えた";
    if (v.deliveries === 1) return "貼り付け済み";
    return "貼り付け中";
  }
  if (v.phase === "delivered") return v.switched ? "届けて元のアプリへ戻る" : "届けた";
  return "クリップボードに残す";
};

export const step = (action, from, to) => {
  if (action === "Done" || action === "Reset") return null;
  if (action === "Tick" && state(from) === state(to)) return null;
  if (action === "ToggleOn" && from.phase !== "idle") return null;
  if (action === "ToggleOff" && from.phase !== "idle" && from.phase !== "armed" && from.phase !== "recording" && from.phase !== "delivering") return null;
  if (action === "Tick") return "3分を過ぎた";
  if (action === "Start") {
    if (to.kind === "secure") return "音声入力を開始 [パスワード欄]";
    if (to.kind === "nofield") return "音声入力を開始 [入力欄がない]";
    if (to.kind === "tmux") return "音声入力を開始 [tmux のペイン]";
    return "音声入力を開始 [テキスト欄]";
  }
  if (action === "Speech") {
    if (!to.alive) return "話し始めた [宛先は既に無い]";
    if (to.switched) return "話し始めた [別のアプリ]";
    return "話し始めた";
  }
  if (action === "SwitchApp") return "別のアプリに移った";
  if (action === "TargetGone") return "宛先が消えた";
  if (action === "StopEmpty") return "空のまま終わった";
  if (action === "StopText") return to.phase === "delivering" ? "テキストができた" : "テキストができた [貼れない]";
  if (action === "PasteOk") return "覚えた場所へ貼った";
  if (action === "PasteFail") return "貼り付けに失敗した";
  if (action === "EnterOk") return from.switched ? "Enter して元のアプリへ戻る" : "Enter を押した";
  if (action === "EnterFail") return "Enter に失敗した";
  if (action === "ToggleOff") return "スティッキーを切った";
  if (action === "ToggleOn") return "スティッキーを入れた";
  if (action === "Reset") return "次の入力を待つ";
  return action;
};
