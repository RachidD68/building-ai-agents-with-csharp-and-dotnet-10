#!/usr/bin/env python3
"""Validate a single expense line item against Contoso spend policy.

Usage:
    validate.py <category> <amount_usd> [meal_scope]

    category   - meals | lodging | ground | air | conference | alcohol | other
    amount_usd - the line-item amount in US dollars
    meal_scope - optional: domestic | international (defaults to domestic);
                 only used to pick the per-day meal limit

Prints a JSON verdict: allowed | requires-approval | not-allowed, with the
rule that produced it. The agent reads references/POLICY_FAQ.md first, then
calls this script to get a deterministic verdict it can quote.
"""
import json
import sys


def verdict(category: str, amount: float, meal_scope: str) -> dict:
    category = category.strip().lower()
    meal_scope = (meal_scope or "domestic").strip().lower()

    # Hard stops first.
    if category in {"first-class", "first class", "cash-advance", "cash advance", "entertainment"}:
        return {"status": "not-allowed", "rule": "Category is on the not-allowed list."}

    # Approval gates that apply regardless of category.
    if amount > 500:
        return {"status": "requires-approval",
                "rule": "Any single line item above USD 500 requires approval."}

    if category == "alcohol":
        return {"status": "requires-approval",
                "rule": "Alcohol is allowed only at customer-facing dinners with manager sign-off."}

    # Category-specific limits.
    if category == "meals":
        limit = 150 if meal_scope == "domestic" else 90
        if amount > limit:
            return {"status": "requires-approval",
                    "rule": f"Meals exceed the {meal_scope} per-day limit of USD {limit}."}
        return {"status": "allowed", "rule": f"Within the {meal_scope} meal limit of USD {limit}/day."}

    if category == "lodging":
        limit = 250 if meal_scope == "domestic" else 200
        if amount > limit:
            return {"status": "requires-approval",
                    "rule": f"Lodging exceeds the {meal_scope} per-night limit of USD {limit}."}
        return {"status": "allowed", "rule": f"Within the {meal_scope} lodging limit of USD {limit}/night."}

    if category in {"ground", "air", "conference"}:
        return {"status": "allowed", "rule": f"{category.capitalize()} is an allowed category within limits."}

    return {"status": "requires-approval",
            "rule": "Category not explicitly covered by policy; route to a human."}


def main() -> None:
    if len(sys.argv) < 3:
        print(json.dumps({"error": "usage: validate.py <category> <amount_usd> [meal_scope]"}))
        return

    category = sys.argv[1]
    try:
        amount = float(sys.argv[2])
    except ValueError:
        print(json.dumps({"error": f"amount '{sys.argv[2]}' is not a number"}))
        return
    meal_scope = sys.argv[3] if len(sys.argv) > 3 else "domestic"

    result = verdict(category, amount, meal_scope)
    result["category"] = category.lower()
    result["amount_usd"] = amount
    result["receipt_required"] = amount > 25
    print(json.dumps(result))


if __name__ == "__main__":
    main()
