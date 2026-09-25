#!/usr/bin/env python3
"""Pair bounded Shock Coil traces by source owner/generation/life/frame, never arrival time.

Reports rejection reasons separately from agreement of accepted decisions. Collision
and damage-gate comparisons are diagnostic attempts, not confirmed health awards.
"""
import argparse
from collections import Counter
import json
from pathlib import Path
import re

REJECTIONS = ['None', 'ExplicitNone', 'MissingDecision', 'Stale', 'Stream', 'WrongGeneration',
              'WrongLife', 'UnavailableHistory', 'Team', 'Range', 'Angle', 'Form', 'Eligibility']

def identity(x):
    return x['EncodedSlot'], x['Generation'], x['LifeId']

def classify(owner, authority):
    a, b = owner['Selected'], authority['Selected']
    if identity(a) != identity(b):
        if not a['HasPlayer']: return 'OwnerTarget_None_AuthorityTarget_Player'
        if not b['HasPlayer']: return 'OwnerTarget_Player_AuthorityTarget_None'
        if a['Slot'] != b['Slot']: return 'DifferentPlayer'
        if a['Generation'] != b['Generation']: return 'WrongGeneration'
        return 'WrongLife'
    if owner['AltForm'] != authority['AltForm'] or owner['Morphing'] != authority['Morphing']: return 'FormDisagreement'
    if any(owner[k] != authority[k] for k in ['Eligible', 'TeamEligible', 'Targetable']): return 'EligibilityDisagreement'
    if (owner['Dot'] >= owner['Threshold']) != (authority['Dot'] >= authority['Threshold']): return 'AngleDisagreement'
    if owner['Phase'] != authority['Phase']: return 'PhaseDisagreement'
    if any(owner.get(k) != authority.get(k) for k in ['CollisionResolved', 'CollisionKind', 'CollisionWinner']): return 'CollisionDisagreement'
    if any(owner[k] != authority[k] for k in ['CollisionTest', 'Overlap']) or owner.get('CollisionTarget') != authority.get('CollisionTarget'): return 'CollisionDisagreement'
    if any(owner[k] != authority[k] for k in ['DamageGate', 'Damage']): return 'DamageGateDisagreement'
    return 'Agreed'

def compare(folder):
    owners = {}
    for path in folder.glob('peer*-targets.jsonl'):
        local = int(path.stem.split('-')[0][4:])
        log = folder / f'peer{local}.log'
        match = re.search(r'joined as slot (\d+)', log.read_text())
        if not match: continue
        local = int(match[1])
        burst = 0; previous = None
        for line in path.read_text().splitlines():
            item = json.loads(line)
            if item['Owner'] != local: continue
            key = (item['Owner'], item['Generation'], item['Life'], item['Frame'])
            if previous is None or item['Frame'] != previous['Frame'] + 1 or item['Life'] != previous['Life']:
                burst = item['Frame']
            item['burst'] = burst; owners[key] = item; previous = item
    authority_path = folder / 'server-targets.jsonl'
    if not authority_path.exists(): return {'run': folder.name, 'unavailable': 'no authority trace'}
    categories, reasons, offsets = Counter(), Counter(), Counter()
    first = {}; accepted = agreed = paired = missing = 0
    timers_equal = damage_pairs = damage_equal = 0
    for line in authority_path.read_text().splitlines():
        item = json.loads(line)
        if not item['Authority']: continue
        reasons[REJECTIONS[item['Rejection']]] += 1
        key = (item['Owner'], item['Generation'], item['Life'], item['ReportFrame'])
        owner = owners.get(key)
        if owner is None: missing += 1; continue
        paired += 1
        timers_equal += owner['Timer'] == item['Timer']
        if owner['DamageGate'] and item['DamageGate']:
            damage_pairs += 1
            damage_equal += owner['Damage'] == item['Damage']
        if item['Rejection'] in (0, 1):
            accepted += 1
            agreed += identity(owner['Selected']) == identity(item['Selected'])
        category = classify(owner, item); categories[category] += 1
        offsets[item['Phase'] - owner['Phase']] += 1
        burst = (item['Owner'], item['Generation'], item['Life'], owner['burst'])
        if category != 'Agreed' and burst not in first:
            first[burst] = {'owner': item['Owner'], 'sourceFrame': item['ReportFrame'],
                            'authorityFrame': item['Frame'], 'burstFrame': owner['burst'],
                            'category': category, 'reason': REJECTIONS[item['Rejection']]}
    return {'run': folder.name, 'pairedEvaluations': paired, 'unpairedEvaluations': missing,
            'acceptedPairedDecisions': accepted, 'acceptedTargetAgreement': agreed,
            'acceptedTargetAgreementPercent': 100 * agreed / accepted if accepted else None,
            'timerComparisons': paired, 'timerAgreement': timers_equal,
            'pairedDamageCalls': damage_pairs, 'pairedDamageAmountAgreement': damage_equal,
            'firstDivergencePerBurst': list(first.values()), 'categories': categories,
            'rejections': reasons, 'phaseOffsets': dict(sorted(offsets.items()))}

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('root', type=Path)
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    folders = [args.root] if (args.root / 'server-targets.jsonl').exists() else sorted(args.root.glob('shockcoil*'))
    result = {'scope': 'bounded per-evaluation traces; received reports may repeat or be absent; no inferred health awards',
              'runs': [compare(folder) for folder in folders]}
    text = json.dumps(result, indent=2) + '\n'
    if args.output: args.output.write_text(text)
    else: print(text, end='')

if __name__ == '__main__': main()
