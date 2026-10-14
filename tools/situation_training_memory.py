"""Exact assistant-position loss and explicit SDPA backend selection for batch one."""
from contextlib import nullcontext


def projection_kwargs(labels, implementation):
    """Select hidden[t-1] for each supervised labels[t], preserving the full input."""
    import torch
    if implementation not in ('full', 'assistant-positions'):
        raise ValueError('unknown-loss-implementation')
    if labels.ndim != 2 or labels.shape[0] != 1 or labels.shape[1] < 2:
        raise ValueError('assistant-projection-requires-single-sequence')
    if labels.dtype != torch.long:
        raise ValueError('assistant-projection-requires-long-labels')
    if labels[0, 0].item() != -100:
        raise ValueError('first-token-cannot-be-supervised')
    positions = torch.where(labels[0, 1:] != -100)[0]
    if positions.numel() == 0:
        raise ValueError('assistant-projection-empty-supervision')
    if implementation == 'full':
        return {}
    return dict(logits_to_keep=positions, shift_labels=labels[:, positions + 1].contiguous())


def sdpa_context(backend):
    """Keep this context around forward AND checkpointed backward; never fallback."""
    if backend == 'auto':
        return nullcontext()
    if backend != 'cudnn':
        raise ValueError('unknown-sdpa-backend')
    from torch.nn.attention import SDPBackend, sdpa_kernel
    return sdpa_kernel([SDPBackend.CUDNN_ATTENTION])


def attention_evidence(profiler, backend):
    """Verify actual model forward/backward operators, not configuration flags."""
    counts = {event.key: event.count for event in profiler.key_averages()
              if 'scaled_dot_product' in event.key}
    if backend == 'cudnn':
        if not counts.get('aten::_scaled_dot_product_cudnn_attention'):
            raise ValueError('cudnn-attention-forward-not-observed')
        if not counts.get('aten::_scaled_dot_product_cudnn_attention_backward'):
            raise ValueError('cudnn-attention-backward-not-observed')
        forbidden = ('math', 'efficient_attention', 'flash_attention')
        if any(any(word in name for word in forbidden) for name in counts):
            raise ValueError('unexpected-sdpa-backend-observed')
    return dict(backend=backend, operators=counts,
                includesCheckpointRecomputation=True)
