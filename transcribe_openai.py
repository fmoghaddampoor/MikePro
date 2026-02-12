import sys
import os
import site
sys.path.append(site.getusersitepackages())
from openai import OpenAI

api_key = sys.argv[2] if len(sys.argv) > 2 else os.environ.get("OPENAI_API_KEY")
if not api_key:
    print("ERROR: No API key provided")
    sys.exit(1)

client = OpenAI(api_key=api_key)
audio_path = sys.argv[1] if len(sys.argv) > 1 else None
if not audio_path:
    print("ERROR: No audio file provided")
    sys.exit(1)

with open(audio_path, "rb") as f:
    result = client.audio.transcriptions.create(
        model="whisper-1",
        file=f
    )
print(result.text)
