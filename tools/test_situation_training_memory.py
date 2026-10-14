"""Loss/gradient equivalence and refusal to silently change supervision/backend."""
from contextlib import nullcontext
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
import torch
from peft import LoraConfig, get_peft_model
from transformers import Qwen3Config, Qwen3ForCausalLM
from situation_training_memory import attention_evidence, projection_kwargs, sdpa_context


class TrainingMemoryTests(unittest.TestCase):
    def test_causal_positions_include_first_answer_eos_and_suffix(self):
        labels = torch.tensor([[-100, -100, -100, 17, 18, 2, 9]])
        before = labels.clone()
        kwargs = projection_kwargs(labels, 'assistant-positions')
        self.assertEqual(kwargs['logits_to_keep'].tolist(), [2, 3, 4, 5])
        self.assertEqual(kwargs['shift_labels'].tolist(), [[17, 18, 2, 9]])
        self.assertTrue(torch.equal(labels, before))
        self.assertEqual(projection_kwargs(labels, 'full'), {})

    def test_noncontiguous_supervision_is_preserved(self):
        kwargs = projection_kwargs(torch.tensor([[-100, 7, -100, 8, 2]]), 'assistant-positions')
        self.assertEqual(kwargs['logits_to_keep'].tolist(), [0, 2, 3])
        self.assertEqual(kwargs['shift_labels'].tolist(), [[7, 8, 2]])

    def test_unsupported_shapes_empty_labels_and_first_token_fail(self):
        for labels in (torch.tensor([-100, 2]), torch.tensor([[-100, 2], [-100, 2]]),
                       torch.tensor([[-100]]), torch.tensor([[-100, -100]]),
                       torch.tensor([[7, 2]]), torch.tensor([[-100., 2.]])):
            with self.subTest(labels=labels):
                with self.assertRaises(ValueError):
                    projection_kwargs(labels, 'assistant-positions')
        with self.assertRaisesRegex(ValueError, 'unknown-loss'):
            projection_kwargs(torch.tensor([[-100, 2]]), 'unknown')

    def test_only_cudnn_backend_is_enabled_and_context_covers_backward(self):
        marker = nullcontext()
        with patch('torch.nn.attention.sdpa_kernel', return_value=marker) as factory:
            self.assertIs(sdpa_context('cudnn'), marker)
            factory.assert_called_once_with([torch.nn.attention.SDPBackend.CUDNN_ATTENTION])
        with self.assertRaisesRegex(ValueError, 'unknown-sdpa'):
            sdpa_context('unknown')

    def test_backend_evidence_requires_forward_and_backward_without_fallback(self):
        def profiler(names):
            return SimpleNamespace(key_averages=lambda: [SimpleNamespace(key=n, count=28) for n in names])
        forward = 'aten::_scaled_dot_product_cudnn_attention'
        backward = forward + '_backward'
        self.assertEqual(attention_evidence(profiler([forward, backward]), 'cudnn')['operators'][backward], 28)
        for names in ([forward], [backward], [forward, backward, 'aten::_scaled_dot_product_attention_math']):
            with self.assertRaises(ValueError):
                attention_evidence(profiler(names), 'cudnn')

    def test_complete_model_loss_all_adapter_gradients_and_update_are_equivalent(self):
        torch.set_num_threads(2)
        torch.manual_seed(42)
        config = Qwen3Config(vocab_size=97, hidden_size=32, intermediate_size=64,
            num_hidden_layers=2, num_attention_heads=4, num_key_value_heads=2,
            head_dim=8, tie_word_embeddings=True, use_cache=False,
            attention_dropout=0.0, eos_token_id=2)
        config._attn_implementation = 'sdpa'
        model = get_peft_model(Qwen3ForCausalLM(config), LoraConfig(task_type='CAUSAL_LM',
            r=4, lora_alpha=8, lora_dropout=0.05, target_modules='all-linear', bias='none'))
        model.gradient_checkpointing_enable(gradient_checkpointing_kwargs={'use_reentrant': False})
        with torch.no_grad():
            for name, p in model.named_parameters():
                if 'lora_B' in name:
                    p.normal_(0, 0.03)
        model.train()
        for length, prefix, holes in ((19, 12, []), (11, 10, []), (23, 9, [13, 17, 20]), (57, 33, [])):
            with self.subTest(length=length):
                ids = torch.randint(3, 97, (1, length))
                ids[0, -1] = 2
                labels = ids.clone()
                labels[:, :prefix] = -100
                for index in holes:
                    labels[:, index] = -100
                batch = dict(input_ids=ids, attention_mask=torch.ones_like(ids), labels=labels)
                before = {k: v.clone() for k, v in batch.items()}
                rng = torch.get_rng_state()
                model.zero_grad(set_to_none=True)
                full = model(**batch, use_cache=False)
                full.loss.backward()
                loss = full.loss.detach().clone()
                grads = {n: p.grad.clone() for n, p in model.named_parameters() if p.requires_grad}
                parameters = {n: p.detach().clone() for n, p in model.named_parameters() if p.requires_grad}
                optimizer = torch.optim.AdamW([p for p in model.parameters() if p.requires_grad], lr=1e-4, foreach=False)
                optimizer.step()
                updates = {n: p.detach().clone() for n, p in model.named_parameters() if p.requires_grad}
                with torch.no_grad():
                    for name, p in model.named_parameters():
                        if p.requires_grad:
                            p.copy_(parameters[name])
                model.zero_grad(set_to_none=True)
                torch.set_rng_state(rng)
                selected = model(**batch, **projection_kwargs(labels, 'assistant-positions'), use_cache=False)
                selected.loss.backward()
                torch.testing.assert_close(selected.loss, loss, rtol=2e-6, atol=2e-7)
                for name, p in model.named_parameters():
                    if p.requires_grad:
                        torch.testing.assert_close(p.grad, grads[name], rtol=2e-5, atol=2e-7)
                    else:
                        self.assertIsNone(p.grad)
                optimizer = torch.optim.AdamW([p for p in model.parameters() if p.requires_grad], lr=1e-4, foreach=False)
                optimizer.step()
                for name, p in model.named_parameters():
                    if p.requires_grad:
                        torch.testing.assert_close(p, updates[name], rtol=2e-5, atol=2e-7)
                for key in batch:
                    self.assertTrue(torch.equal(batch[key], before[key]))


if __name__ == '__main__':
    unittest.main()
