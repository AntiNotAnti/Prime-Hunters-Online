import copy
import json
from pathlib import Path
import tempfile
import unittest
from collector import valid
from report import distribution, read_matches, render, summarize

class ReportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.fixture = json.loads(Path('docs/network/validation/protocol19-2026-09-24.json').read_text())['eightPlayerPerformance']['runs'][1]['fullMatchTelemetry']
        # This small source-backed fixture supplies only selected fields; fill structurally required arrays.
        cls.fixture.update(network=[0]*8, combat=[0]*8, claims=[0]*16, lifecycle=[0]*256, lagComp=[])

    def test_weighted_mean_does_not_average_percentiles(self):
        a=dict(count=1,mean=10,p50=10,p95=10,p99=10,maximum=10)
        b=dict(count=9,mean=100,p50=100,p95=100,p99=100,maximum=100)
        s=distribution([a,b]);self.assertEqual(s['mean'],91);self.assertEqual(s['matchP95Range'],[10,100])
        self.assertIsNone(distribution([])['mean'])

    def test_dedup_and_validation(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder)
            for i in range(2):(root/f'match-{i}.json').write_text(json.dumps(self.fixture))
            (root/'match-invalid.json').write_text('{')
            matches,rejected,duplicates=read_matches(root)
            self.assertEqual((len(matches),len(rejected),duplicates),(1,1,1))

    def test_private_keys_and_nonfinite_rejected(self):
        x=copy.deepcopy(self.fixture);x['ip']='example';self.assertFalse(valid(x))
        x=copy.deepcopy(self.fixture);x['combatAckLatency']['mean']=float('inf');self.assertFalse(valid(x))

    def test_current_protocol_twenty_is_accepted(self):
        x=copy.deepcopy(self.fixture);x['header']['protocol']=20
        self.assertTrue(valid(x))
        x['header']['protocol']=21;self.assertFalse(valid(x))

    def test_schema_two_and_bucket_bounds(self):
        x=copy.deepcopy(self.fixture);x['header']['schema']=2
        d=dict(count=1,mean=10,p50=10,p95=10,p99=10,maximum=10)
        x['networkDetails']={k:copy.deepcopy(d) for k in ('rttMilliseconds','jitterMilliseconds','recentMinimumRttMilliseconds','rttVariationMilliseconds')}
        x['networkDetails'].update(rttBuckets=[1]+[0]*8,jitterBuckets=[1]+[0]*5,retransmissions=2,estimatedLost=3,queueHighWater=4)
        x['lifecycleDetails']={k:copy.deepcopy(d) for k in ('joinMilliseconds','loadMilliseconds','bootstrapMilliseconds','rejoinMilliseconds')}
        x['lifecycleDetails'].update(ready=1,lateJoins=0,disconnects=1)
        x['combatDetails']=dict(settledPredictions=1,exactDamagePredictions=1,damageCorrections=0,headshotCorrections=0,healthCorrections=0,rejectedPredictions=0)
        x['shadowOutcomes']=[0]*6+[1];x['formCorrectionReasons']=[0]*7
        x['lagComp']=[dict(weapon=11,rttBucket=8,jitterBucket=5,requested=d,plausible=d,displacement=d,
            globalClamps=0,shadowClamps=0,hitsOutside=0,rescuesOutside=0,missesOutside=0,hitsInside=1,rescuesInside=0,missesInside=0,unknownOutcomes=1)]
        self.assertTrue(valid(x));summary=summarize([x]);self.assertEqual(summary['rttBuckets'][0],1)
        self.assertEqual(summary['lagComp'][0]['weapon'],11)
        x['lagComp'][0]['weapon']=12;self.assertFalse(valid(x))
        x['lagComp'][0]['weapon']=1.5;self.assertFalse(valid(x))

    def test_legacy_missing_values_and_injection(self):
        s=summarize([self.fixture]);self.assertIsNone(s['rttBuckets']);self.assertIsNone(s['combat'])
        value=render({'cohorts':[], 'evil':'</script><script>alert(1)</script>'}, '<script>')
        self.assertNotIn('</script><script>',value);self.assertIn('\\u003c/script>',value)

if __name__=='__main__':unittest.main()
