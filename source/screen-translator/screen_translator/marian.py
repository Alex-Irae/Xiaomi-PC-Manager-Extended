"""Run the bundled Marian ONNX graphs without importing the training framework.

Dependencies: existing OpenVINO, NumPy and SentencePiece (no Transformers, no Torch).
Outputs: greedy translations in memory and OpenVINO's SSD compilation cache.
Run through the native app, or python -m screen_translator.cli pipeline.
"""
import json
import os
import sys
from pathlib import Path
from time import perf_counter

import numpy as np


class Pieces:
    """The bundle's Marian tokenizer, using its two SentencePiece models directly.

    Importing the Transformers tokenizer cost 2 s warm and up to 25 s cold for the same
    result. Encoding: source pieces -> vocabulary ids + end token, padded per batch.
    Decoding: ids without special tokens -> target pieces -> text, with the same
    punctuation clean-up the bundle's tokenizer_config requests. A literal "</s>",
    "<unk>" or "<pad>" typed in the source text is treated as ordinary characters.
    """
    CLEAN = ((" .", "."), (" ?", "?"), (" !", "!"), (" ,", ","), (" ' ", "'"), (" n't", "n't"),
             (" 'm", "'m"), (" 's", "'s"), (" 've", "'ve"), (" 're", "'re"))

    def __init__(self, path):
        import sentencepiece
        path = Path(path)
        settings = json.loads((path / "tokenizer_config.json").read_text(encoding="utf-8"))
        if settings.get("tokenizer_class") != "MarianTokenizer" or settings.get("separate_vocabs"):
            raise ValueError("Unsupported tokenizer configuration")
        self.vocabulary = json.loads((path / "vocab.json").read_text(encoding="utf-8"))
        self.words = {number: token for token, number in self.vocabulary.items()}
        self.source = sentencepiece.SentencePieceProcessor(model_file=str(path / "source.spm"))
        self.target = sentencepiece.SentencePieceProcessor(model_file=str(path / "target.spm"))
        self.unknown, self.end, self.pad = (self.vocabulary[settings[name]] for name in ("unk_token", "eos_token", "pad_token"))
        self.special = {self.unknown, self.end, self.pad}
        self.clean = settings.get("clean_up_tokenization_spaces", True)

    def __call__(self, texts, **unused):
        rows = [[self.vocabulary.get(piece, self.unknown) for piece in self.source.encode(text, out_type=str)] + [self.end] for text in texts]
        width = max(len(row) for row in rows)
        # Right-pad every row to the longest; the mask marks real tokens.
        ids = np.full((len(rows), width), self.pad, dtype=np.int64)  # shape: [B,L]
        mask = np.zeros((len(rows), width), dtype=np.int64)  # shape: [B,L]
        for index, row in enumerate(rows):
            ids[index, :len(row)] = row
            mask[index, :len(row)] = 1
        return {"input_ids": ids, "attention_mask": mask}

    def batch_decode(self, sequences, **unused):
        results = []
        for sequence in sequences:
            pieces = [self.words[int(number)] for number in sequence if int(number) not in self.special]
            text = self.target.decode_pieces(pieces).replace("\u2581", " ").strip()
            if self.clean:
                for old, new in self.CLEAN:
                    text = text.replace(old, new)
            results.append(text)
        return results


class Marian:
    """Batch greedy decoding for the stateless encoder/decoder/KV-cache bundle.

    Matches the existing num_beams=1, do_sample=False, 256-token generation.
    Inputs are text strings; outputs are English strings in the same order.
    KV tensors keep their named [batch, heads, sequence, head_dim] layout.
    """

    def __init__(self, path, core, device, options, progress):
        from .runtime import execution_info

        path = Path(path)
        self.tokenizer = Pieces(path)
        self.config = json.loads((path / "config.json").read_text(encoding="utf-8"))
        self.generation = json.loads((path / "generation_config.json").read_text(encoding="utf-8"))
        if self.config["model_type"] != "marian" or any(len(word) != 1 for word in self.generation.get("bad_words_ids", [])):
            raise ValueError("Unsupported Marian generation configuration")
        self.parts = {}
        self.components = {}
        start = perf_counter()
        for name in ("encoder", "decoder", "decoder_with_past"):
            progress(f"Restoring translation {name} on {device}")
            # The path overload lets OpenVINO import a matching SSD blob directly.
            model = core.compile_model(str(path / (name + "_model.onnx")), device, options)
            self.parts[name] = model
            self.components[name] = execution_info(model)
        self.load_compile_ms = (perf_counter() - start) * 1000
        self.last_tokens = 0

    def generate(self, texts):
        if not texts:
            return []
        tokens = self.tokenizer(texts, padding=True, truncation=False, return_tensors="np")  # each [B,L]
        if tokens["input_ids"].shape[1] > 512:
            raise ValueError("Translation unit exceeds 512 tokens; split the source region")
        encoder = self.parts["encoder"]
        encoded = encoder({port: tokens[next(name for name in port.get_names() if name in tokens)] for port in encoder.inputs})
        hidden = encoded[encoder.output(0)]  # [B,L,512], encoder context
        batch = len(texts)
        pad, eos = self.generation["pad_token_id"], self.generation["eos_token_id"]
        # Every sequence begins with Marian's decoder-start token.
        generated = np.full((batch, 1), self.generation["decoder_start_token_id"], dtype=np.int64)  # [B,1]
        finished = np.zeros(batch, dtype=bool)  # [B], independent EOS state
        past = {}
        blocked = [word[0] for word in self.generation.get("bad_words_ids", [])]
        for step in range(256):
            name = "decoder" if step == 0 else "decoder_with_past"
            model = self.parts[name]
            # With KV caching only the newest token is decoded on later steps.
            inputs = {"input_ids": generated[:, -1:], "encoder_hidden_states": hidden,
                      "encoder_attention_mask": tokens["attention_mask"], **past}
            # OpenVINO tensors can have several aliases. Match the canonical ONNX
            # names rather than get_any_name(), which can select an internal alias.
            outputs = model({port: inputs[next(name for name in port.get_names() if name in inputs)] for port in model.inputs})
            values = {next(name for name in port.get_names() if name == "logits" or name.startswith("present.")): value
                      for port, value in outputs.items()}
            logits = values["logits"][:, -1, :].copy()  # [B,V], next-token scores
            logits[:, blocked] = -np.inf  # suppress the same forbidden one-token words
            # Greedy argmax agrees with the existing beam=1 policy; no sampling.
            next_token = np.argmax(logits, axis=1).astype(np.int64)  # [B]
            if step == 255:
                next_token.fill(self.generation.get("forced_eos_token_id", eos))
            next_token[finished] = pad
            generated = np.concatenate((generated, next_token[:, None]), axis=1)  # [B,T+1]
            finished |= next_token == eos
            if finished.all():
                break
            # Decoder-only KV changes; retain the constant encoder cross-attention KV.
            past.update({key.replace("present.", "past_key_values.", 1): value
                         for key, value in values.items() if key.startswith("present.")})
        if not finished.all():
            raise RuntimeError("Translation reached its token limit without completing")
        self.last_tokens = int(batch * (generated.shape[1] - 1))
        return self.tokenizer.batch_decode(generated, skip_special_tokens=True)
