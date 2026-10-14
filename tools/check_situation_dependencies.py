"""Offline CPU-only integration smoke; random tiny Qwen3, synthetic text.

This does not load the 1.7B base, inspect real datasets or qualify GPU memory.
"""
import argparse
import copy
import importlib.metadata
import json
import math
import os
from pathlib import Path
import sys
import uuid


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output',required=True)
    args=parser.parse_args()
    output=Path(args.output)
    if output.exists():
        raise ValueError('output-already-exists')
    os.environ.update(HF_HUB_OFFLINE='1',HF_DATASETS_OFFLINE='1',WANDB_DISABLED='true',TOKENIZERS_PARALLELISM='false')
    import torch
    from datasets import Dataset
    from jsonschema import Draft202012Validator
    from peft import LoraConfig, TaskType, get_peft_model
    from tokenizers import Tokenizer
    from tokenizers.models import WordLevel
    from tokenizers.pre_tokenizers import Whitespace
    from transformers import AutoModelForCausalLM, PreTrainedTokenizerFast, Qwen3Config
    from trl import SFTConfig, SFTTrainer

    assert sys.version_info[:2]==(3,11)
    assert torch.version.cuda=='12.6', 'unexpected CUDA wheel'
    torch.set_num_threads(2)
    torch.manual_seed(42)
    vocab={'[PAD]':0,'[UNK]':1,'[EOS]':2,'fact':3,'one':4,'two':5,'summary':6,'known':7}
    backend=Tokenizer(WordLevel(vocab=vocab,unk_token='[UNK]'))
    backend.pre_tokenizer=Whitespace()
    tokenizer=PreTrainedTokenizerFast(tokenizer_object=backend,pad_token='[PAD]',unk_token='[UNK]',eos_token='[EOS]')
    tokenizer.model_input_names=['input_ids','attention_mask']
    config=Qwen3Config(vocab_size=32,hidden_size=32,intermediate_size=64,num_hidden_layers=1,
        num_attention_heads=4,num_key_value_heads=2,head_dim=8,max_position_embeddings=64,
        pad_token_id=0,eos_token_id=2,bos_token_id=2,tie_word_embeddings=True,use_cache=False)
    config._attn_implementation='eager'
    model=AutoModelForCausalLM.from_config(config)
    model=get_peft_model(model,LoraConfig(task_type=TaskType.CAUSAL_LM,r=4,lora_alpha=8,
                                        lora_dropout=0.0,target_modules='all-linear',bias='none'))
    names=[n for n,p in model.named_parameters() if p.requires_grad]
    assert names and all('lora_' in n for n in names)
    base_before={n:p.detach().clone() for n,p in model.named_parameters() if not p.requires_grad}
    adapter_before={n:p.detach().clone() for n,p in model.named_parameters() if p.requires_grad}
    dataset=Dataset.from_list([{'prompt':'fact one ','completion':'summary known'},
                              {'prompt':'fact two ','completion':'summary known'}])
    training=SFTConfig(output_dir=str(output.parent/(output.stem+'-tiny-cpu-'+uuid.uuid4().hex[:8])),
        use_cpu=True,bf16=False,fp16=False,per_device_train_batch_size=1,gradient_accumulation_steps=1,
        max_steps=1,learning_rate=1e-3,max_length=32,completion_only_loss=True,packing=False,
        gradient_checkpointing=False,optim='adamw_torch',save_strategy='no',logging_strategy='no',
        report_to='none',disable_tqdm=True,dataloader_pin_memory=False,seed=42)
    trainer=SFTTrainer(model=model,args=training,train_dataset=dataset,processing_class=tokenizer)
    batch=next(iter(trainer.get_train_dataloader()))
    assert (batch['labels']!=-100).any(), 'empty supervision'
    assert (batch['labels']==-100).any(), 'prompt masking missing'
    result=trainer.train()
    assert math.isfinite(result.training_loss) and trainer.state.global_step==1
    assert any(not torch.equal(p.detach(),adapter_before[n]) for n,p in model.named_parameters() if n in adapter_before)
    assert all(torch.equal(p.detach(),base_before[n]) for n,p in model.named_parameters() if n in base_before)
    assert all(p.device.type=='cpu' for p in model.parameters())
    model.eval()
    tokens=tokenizer('fact one',return_tensors='pt')
    with torch.no_grad():
        generated=model.generate(**tokens,max_new_tokens=2,do_sample=False,pad_token_id=0)
    assert generated.shape[1]>tokens['input_ids'].shape[1]
    schema=Path(__file__).resolve().parents[1]/'schemas/situation/situation-narrative-v1.schema.json'
    validator=Draft202012Validator(json.loads(schema.read_text(encoding='utf-8')))
    Draft202012Validator.check_schema(validator.schema)
    narrative=dict(schemaVersion='situation-narrative-v1',highlights=[
        dict(textZh='人数已知',evidenceIds=['alive']),dict(textZh='包状态已知',evidenceIds=['bomb'])],
        summaryZh='当前事实已知。',uncertainties=[])
    validator.validate(narrative)
    invalid=copy.deepcopy(narrative)
    invalid['winProbability']=0.5
    assert list(validator.iter_errors(invalid))
    # Evidence-ID checks are separate from JSON shape validation.
    def evidence_valid(value):
        return all(e in {'alive','bomb'} for h in value['highlights'] for e in h['evidenceIds'])
    assert evidence_valid(narrative)
    invalid=copy.deepcopy(narrative)
    invalid['highlights'][0]['evidenceIds']=['missing']
    assert not evidence_valid(invalid)
    versions={d.metadata['Name'].lower().replace('_','-'):d.version for d in importlib.metadata.distributions()}
    payload=dict(status='passed',scope='synthetic-tiny-Qwen3-CPU-API-only',python=sys.version.split()[0],
        cudaRuntime=torch.version.cuda,installedVersions=dict(sorted(versions.items())),
        trainableParameters=sum(p.numel() for p in model.parameters() if p.requires_grad),
        totalParameters=sum(p.numel() for p in model.parameters()),trainingLoss=result.training_loss,
        checks=['Qwen3-native-class','all-linear-LoRA','TRL-SFTTrainer','completion-mask',
                'finite-loss','adapter-updated','base-frozen','CPU-only','short-generation',
                'Draft2020-12-schema','unknown-field-rejected','unknown-evidence-rejected'],
        realDataUsed=False,baseDownloaded=False,gpuProbePassed=False)
    with output.open('x',encoding='utf-8') as f:
        json.dump(payload,f,ensure_ascii=False,indent=2)
        f.write('\n')
    print(json.dumps(dict(status='passed',checks=len(payload['checks']),parameters=payload['totalParameters'])))


if __name__=='__main__':
    main()
