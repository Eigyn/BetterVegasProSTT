"""Conservative Qwen context repair at padded, overlapping ASR chunk edges."""
import math
import statistics

VERSION = "joined-boundary-v1"


def boundary_windows(pieces, groups):
    plans = []
    for index in range(1, len(groups)):
        left, right = groups[index - 1], groups[index]
        if len(left) < 4 or len(right) < 5: continue
        if pieces[index]["Start"] >= pieces[index - 1]["End"]: continue
        edge = left[-1]["End"]
        # A normally aligned overlapping reply must keep its timing. Restrict the
        # retry to a pinned leading token followed by an abnormally stretched word.
        if abs(right[0]["Start"] - pieces[index]["Start"]) > .08: continue
        if right[0]["Start"] >= edge - .15: continue
        typical = statistics.median(w["End"] - w["Start"] for w in right[3:10])
        suspicious = [i for i, w in enumerate(right[:3]) if w["Start"] < edge - .08 and
                      w["End"] > edge + .2 and w["End"] - w["Start"] > max(.8, 3 * typical)]
        if not suspicious: continue
        count = max(suspicious) + 1
        context = left[-8:] + right[:max(8, count + 3)]
        start = max(0, min(w["Start"] for w in context) - .2)
        end = min(pieces[index]["End"], max(w["End"] for w in context) + .2)
        if end - start > 25 or end <= start: continue
        plans.append({"Group": index, "Count": count, "Left": min(8, len(left)), "Context": context,
                      "Piece": {"Start": start, "End": end, "Text": " ".join(w["Text"] for w in context)}})
    return plans


def apply_boundaries(groups, plans, aligned):
    result = [[dict(w) for w in group] for group in groups]
    for plan, refined in zip(plans, aligned):
        old, left, count = plan["Context"], plan["Left"], plan["Count"]
        if len(refined) != len(old) or [w["Text"] for w in refined] != [w["Text"] for w in old]: continue
        if any(not math.isfinite(w[k]) for w in refined for k in ("Start", "End")): continue
        if any(w["End"] <= w["Start"] for w in refined): continue
        if any(b["Start"] < a["End"] - .03 for a, b in zip(refined, refined[1:])): continue
        # Both sides must independently corroborate the new window. Re-aligning
        # an overlap into a completely different phrase is not an accepted repair.
        anchors = list(range(left)) + list(range(left + count, len(old)))
        errors = [abs(refined[i][k] - old[i][k]) for i in anchors for k in ("Start", "End")]
        if not errors or statistics.median(errors) > .06 or max(errors) > .18: continue
        replacements = refined[left:left + count]
        if any(not .03 <= w["End"] - w["Start"] <= .8 for w in replacements): continue
        if any(new["Start"] < previous["Start"] + .25 for previous, new in zip(old[left:left + count], replacements)): continue
        group = result[plan["Group"]]
        for i, replacement in enumerate(replacements):
            group[i]["Start"], group[i]["End"] = replacement["Start"], replacement["End"]
            # The audio-level pass sees the mixed recording, so it cannot safely
            # improve these independently corroborated Qwen boundary estimates.
            group[i]["_KeepAlignment"] = True
    return result
