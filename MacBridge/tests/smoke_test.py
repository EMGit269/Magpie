"""Live integration tests; run through run_smoke.py to isolate the user's canvas."""
import concurrent.futures
import json
import unittest
import urllib.error
import urllib.request

BASE = 'http://127.0.0.1:8765'


def request(path, payload=None, raw=None):
    body = raw if raw is not None else (json.dumps(payload).encode() if payload is not None else None)
    req = urllib.request.Request(BASE + path, data=body, headers={'Content-Type': 'application/json'})
    with urllib.request.urlopen(req, timeout=15) as response:
        return json.load(response)


def invoke(tool, **args):
    return request('/tools/invoke', {'tool': tool, 'args': args, 'request_id': 'mac-smoke'})


class MacBridgeSmoke(unittest.TestCase):
    def ok(self, tool, **args):
        result = invoke(tool, **args)
        self.assertEqual(result['status'], 'ok', result)
        self.assertEqual(result['request_id'], 'mac-smoke')
        return result['result']

    def test_01_manifest_and_errors(self):
        self.assertEqual(request('/health')['status'], 'ok')
        tools = {tool['name'] for tool in request('/tools/manifest')['tools']}
        self.assertEqual(tools, {
            'search_component_catalog', 'get_canvas_summary', 'query_components', 'get_component_context',
            'check_gh_errors', 'recompute_canvas', 'connect_components',
            'remove_component', 'set_component_value', 'create_component_graph'})
        self.assertEqual(invoke('edit_csharp_script', id='01', body='')['status'], 'error')
        self.assertEqual(invoke('query_components', max_results=100)['status'], 'error')
        with self.assertRaises(urllib.error.HTTPError) as failure:
            request('/tools/invoke', raw=b'{invalid')
        self.assertEqual(failure.exception.code, 400)
        with self.assertRaises(urllib.error.HTTPError) as failure:
            request('/missing')
        self.assertEqual(failure.exception.code, 404)

    def test_02_document_lifecycle(self):
        self.assertEqual(self.ok('get_canvas_summary')['component_count'], 0)
        graph = self.ok('create_component_graph', components=[
            {'name': 'Number Slider', 'alias': 'n', 'x': 100, 'y': 100, 'value': '0.5'},
            {'name': 'Panel', 'alias': 'p', 'x': 320, 'y': 100},
            {'name': 'Boolean Toggle', 'alias': 'b', 'x': 100, 'y': 260, 'value': 'true'},
        ], connections=[{'from_alias': 'n', 'to_alias': 'p'}], group_name='Magpie smoke')
        self.assertEqual(graph['created_count'], 3)
        ids = graph['aliases']
        summary = self.ok('get_canvas_summary')
        self.assertEqual(summary['component_count'], 3)
        self.assertEqual(summary['group_count'], 1)
        slider = next(c for c in summary['components'] if c['guid'] == ids['n'])
        hits = self.ok('query_components', id=slider['id'], neighbor_depth=1)['hits']
        self.assertEqual(len(hits), 2)
        context = self.ok('get_component_context', id=ids['p'], depth=1)
        self.assertEqual(context['context_count'], 2)
        self.ok('set_component_value', id=slider['id'], value='7.25', min=0, max=10, decimals=2)
        self.assertEqual(invoke('set_component_value', id=slider['id'], value='11')['status'], 'error')
        self.ok('set_component_value', id=ids['b'], value='false')
        self.ok('connect_components', from_id=ids['b'], to_id=ids['p'])
        self.ok('connect_components', from_id=ids['b'], to_id=ids['p'])
        panel = self.ok('query_components', id=ids['p'])['hits'][0]
        self.assertEqual(panel['inputs'][0]['source_count'], 2, 'Connecting twice must be idempotent')
        self.assertEqual(invoke('connect_components', from_id=ids['n'], to_id=ids['p'], to_index=9)['status'], 'error')
        self.ok('recompute_canvas')
        self.ok('check_gh_errors')
        self.ok('remove_component', id=ids['n'])
        self.assertEqual(self.ok('get_canvas_summary')['component_count'], 2)
        panel = self.ok('query_components', id=ids['p'])['hits'][0]
        self.assertEqual(panel['inputs'][0]['source_count'], 1)
        self.ok('remove_component', id=ids['b'])
        self.ok('remove_component', id=ids['p'])

    def test_03_validation_is_atomic(self):
        before = self.ok('get_canvas_summary')['component_count']
        for components, wires in [
            ([{'name': 'Panel', 'alias': 'p', 'x': 0, 'y': 0}], [{'from_alias': 'missing', 'to_alias': 'p'}]),
            ([{'name': 'Panel', 'alias': 'p'}, {'name': 'Not an installed component'}], []),
            ([{'name': 'Panel', 'alias': 'p'}, {'name': 'Panel', 'alias': 'p'}], []),
        ]:
            self.assertEqual(invoke('create_component_graph', components=components, connections=wires)['status'], 'error')
            self.assertEqual(self.ok('get_canvas_summary')['component_count'], before)

    def test_04_circle_creation(self):
        catalog = self.ok('search_component_catalog', query='Circle', max_results=20)
        circle = next(c for c in catalog['matches'] if c['name'] == 'Circle' and c['is_component'])
        self.assertEqual([p['name'] for p in circle['inputs']], ['Plane', 'Radius'])
        self.assertTrue(circle['component_guid'])
        before = self.ok('get_canvas_summary')['component_count']
        # An exact primary name must win over the Circle parameter and other nicknames.
        for spec in [
            {'value': '15'},
            {'value': '25', 'min': 5, 'max': 30, 'decimals': 2},
            {'value': '-12', 'min': -50, 'max': 50},
        ]:
            graph = self.ok('create_component_graph', components=[
                {'name': 'Number Slider', 'alias': 'r', 'x': 0, 'y': 0, **spec},
                {'name': 'Circle', 'alias': 'c', 'x': 200, 'y': 0},
            ], connections=[{'from_alias': 'r', 'to_alias': 'c', 'to_index': 1}])
            aliases = graph['aliases']
            component = next(c for c in graph['components'] if c['guid'] == aliases['c'])
            self.assertEqual([p['name'] for p in component['inputs']], ['Plane', 'Radius'])
            if float(spec['value']) > 0:
                self.assertEqual(component['outputs'][0]['data_count'], 1)
                preview = component['outputs'][0]['preview'][0]
                self.assertTrue(preview['is_valid'])
                self.assertAlmostEqual(preview['radius'], float(spec['value']))
            self.ok('remove_component', id=aliases['c'])
            self.ok('remove_component', id=aliases['r'])
        invalid = invoke('create_component_graph', components=[
            {'name': 'Number Slider', 'value': '40', 'min': 5, 'max': 30}], connections=[])
        self.assertEqual(invalid['status'], 'error')
        self.assertEqual(self.ok('get_canvas_summary')['component_count'], before)

    def test_05_parallel_ui_dispatch(self):
        with concurrent.futures.ThreadPoolExecutor(max_workers=8) as executor:
            replies = list(executor.map(lambda _: invoke('get_canvas_summary'), range(24)))
        self.assertTrue(all(reply['status'] == 'ok' for reply in replies), replies)


if __name__ == '__main__':
    unittest.main(verbosity=2)
