"""Validate UPM dependency boundaries and Unity metadata without importing Unity.

Run with Python 3.10+. Policy changes are reviewable alongside assembly changes.
An optional --workspace directory additionally checks cycles across installed packages.
"""
import argparse
import json
from pathlib import Path
import re
import sys

def load(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))

def validate(root):
    errors = []
    package = load(root / 'package.json')
    policy = load(root / 'package-policy.json')
    if not package.get('name', '').startswith('com.juahn.v2.'):
        errors.append('Invalid package name')
    if not re.fullmatch(r'\d+\.\d+\.\d+(?:-[\w.-]+)?', package.get('version', '')):
        errors.append('Invalid package version')
    if package.get('license') != 'MIT' or not package.get('unity'):
        errors.append('Missing Unity compatibility or MIT declaration')
    actual = {load(p)['name']: (p, load(p)) for p in (root / 'Runtime').rglob('*.asmdef')}
    if len(actual) != len(list((root / 'Runtime').rglob('*.asmdef'))):
        errors.append('Duplicate runtime assembly names')
    expected = policy['assemblies']
    if set(actual) != set(expected):
        errors.append('Runtime assembly set differs from package-policy.json')
    graph = {}
    for name, (path, assembly) in actual.items():
        rules = expected.get(name, {})
        refs = assembly.get('references', [])
        graph[name] = [r for r in refs if r in actual]
        if set(refs) != set(rules.get('references', [])):
            errors.append(name + ': reference set differs from policy')
        if assembly.get('noEngineReferences', False) != rules.get('noEngineReferences', False):
            errors.append(name + ': engine-reference policy violated')
        if assembly.get('noEngineReferences'):
            for source in path.parent.rglob('*.cs'):
                if re.search(r'\b(?:using\s+(?:static\s+)?|global::)(?:UnityEngine|UnityEditor)\b', source.read_text()):
                    errors.append(str(source.relative_to(root)) + ': engine namespace in pure assembly')
        for ref in refs:
            if ref in actual:
                continue
            owner = policy.get('assemblyOwners', {}).get(ref)
            if not owner:
                errors.append(name + ': undocumented external assembly ' + ref)
            elif owner not in package.get('dependencies', {}) and owner not in policy.get('consumerDependencies', []):
                errors.append(name + ': dependency is neither declared nor consumer-managed: ' + owner)
            if ref.startswith(('Game.', 'Perbase.', 'Percent.', 'Quantum.', 'IdleMine.')):
                errors.append(name + ': game-specific assembly reference ' + ref)
    seen, active = set(), set()
    def visit(name):
        if name in active:
            errors.append('Assembly cycle: ' + name)
            return
        if name in seen:
            return
        active.add(name)
        for dependency in graph.get(name, []):
            visit(dependency)
        active.remove(name)
        seen.add(name)
    for name in graph:
        visit(name)
    guids = {}
    for path in root.rglob('*'):
        if any(part.startswith('.') or part.endswith('~') for part in path.relative_to(root).parts):
            continue
        if path.suffix == '.meta':
            match = re.search(r'^guid: ([0-9a-f]{32})$', path.read_text(), re.M)
            if not match:
                errors.append('Invalid Unity GUID: ' + str(path.relative_to(root)))
            elif match[1] in guids:
                errors.append('Duplicate Unity GUID: ' + str(path.relative_to(root)))
            else:
                guids[match[1]] = path
        elif not path.name.endswith('.meta') and not path.with_name(path.name + '.meta').exists():
            errors.append('Missing .meta: ' + str(path.relative_to(root)))
    return package['name'], errors

def workspace_cycles(root):
    packages = {}
    for manifest in root.glob('com.*/package.json'):
        p = load(manifest)
        packages[p['name']] = p.get('dependencies', {})
    active, seen = set(), set()
    def visit(name):
        if name in active:
            raise ValueError('Package dependency cycle: ' + name)
        if name in seen:
            return
        active.add(name)
        for target in packages[name]:
            if target in packages:
                visit(target)
        active.remove(name)
        seen.add(name)
    for name in packages:
        visit(name)

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument('--workspace', type=Path)
    args = parser.parse_args()
    try:
        name, errors = validate(args.root)
        if args.workspace:
            workspace_cycles(args.workspace)
        if errors:
            print('\n'.join(errors), file=sys.stderr)
            sys.exit(1)
        print(name + ': metadata, assembly boundaries and package dependencies passed')
    except (ValueError, KeyError, OSError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
