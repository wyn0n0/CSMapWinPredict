import math
import unittest
import torch
from situation_stage6_weights import build_epoch_entries, weighted_sample_mean_loss

class WeightTests(unittest.TestCase):
    def record(self,key,human=False,round='r'):
        return dict(sampleId=key,metadata=dict(split='train',matchRef='m',roundRef=round),
                    labelSource='human-approved' if human else 'template-prelabel',reviewStatus='approved' if human else 'unreviewed')

    def test_expansion_and_round_totals(self):
        rows=build_epoch_entries([self.record('h',True),self.record('t'),self.record('u',round='s')],
             {'human_train_total_occurrences_per_epoch':5})
        self.assertEqual(len(rows),7)
        self.assertEqual(sum(r['sampleId']=='h' for r in rows),5)
        self.assertAlmostEqual(sum(r['metadata']['sampleWeight'] for r in rows if r['metadata']['roundRef']=='r'),1)
        self.assertEqual(rows[-1]['metadata']['sampleWeight'],1)
        self.assertTrue(all(r['metadata']['weightDenominator']==6 for r in rows[:-1]))

    def test_cpu_numerical_batch_one_and_accumulation(self):
        # assistant and EOS are supervised; prompt loss must never contribute.
        losses=torch.tensor([[900.,2.,4.],[500.,8.,10.]],dtype=torch.float64,requires_grad=True)
        mask=torch.tensor([[False,True,True],[False,True,True]])
        full=weighted_sample_mean_loss(losses,mask,[1/6,1/6],normalizer=2)
        micro=sum(weighted_sample_mean_loss(losses[i:i+1],mask[i:i+1],[1/6],normalizer=2) for i in range(2))
        self.assertAlmostEqual(full.item(),1.)
        self.assertEqual(full.item(),micro.item())
        self.assertAlmostEqual(weighted_sample_mean_loss(losses[:1],mask[:1],[1/6],normalizer=1).item(),.5)
        full.backward()
        self.assertEqual(losses.grad[0,0].item(),0)
        self.assertAlmostEqual(losses.grad[0,2].item(),1/24)

    def test_no_double_five(self):
        rows=build_epoch_entries([self.record('h',True),self.record('t')],{'human_train_total_occurrences_per_epoch':5})
        values=torch.tensor([[3.] if r['sampleId']=='h' else [9.] for r in rows])
        result=weighted_sample_mean_loss(values,torch.ones_like(values,dtype=torch.bool),[r['metadata']['sampleWeight'] for r in rows],normalizer=1)
        self.assertAlmostEqual(result.item(),4.)

    def test_rejects(self):
        for rows,policy in [([],5),([self.record('a'),self.record('a')],5),([self.record('a')],6)]:
            with self.assertRaises(ValueError): build_epoch_entries(rows,{'human_train_total_occurrences_per_epoch':policy})
        row=self.record('a');row['metadata']['split']='dev'
        with self.assertRaises(ValueError): build_epoch_entries([row],{'human_train_total_occurrences_per_epoch':5})
        losses=torch.ones((1,2));mask=torch.ones((1,2),dtype=torch.bool)
        for weights,normalizer in [([0],1),([float('nan')],1),([1],0),([1],float('inf'))]:
            with self.assertRaises(ValueError): weighted_sample_mean_loss(losses,mask,weights,normalizer=normalizer)
        with self.assertRaises(ValueError): weighted_sample_mean_loss(losses,~mask,[1],normalizer=1)
        with self.assertRaises(ValueError): weighted_sample_mean_loss(losses*float('nan'),mask,[1],normalizer=1)

if __name__=='__main__': unittest.main()
