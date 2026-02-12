import sys
import site
sys.path.append(site.getusersitepackages())
from faster_whisper import WhisperModel

model_size = sys.argv[2] if len(sys.argv) > 2 else "small"
model = WhisperModel(model_size, device="cpu", compute_type="int8")

audio_path = sys.argv[1] if len(sys.argv) > 1 else None
if not audio_path:
    print("ERROR: No audio file provided")
    sys.exit(1)

segments, info = model.transcribe(audio_path, language="en")
text = " ".join([seg.text for seg in segments])
print(text.strip())
