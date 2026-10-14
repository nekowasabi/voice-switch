import Foundation
print("#tool agree_pair")  // tool_io.py checks this header
// For app_eval3.py on PR #38 and later. stdin: apple<TAB>whisper. stdout: agreed character count (agreedWithWhisper).
while let l = readLine() { let p = l.components(separatedBy: "\t"); print(NoWakePolicy.agreedWithWhisper(apple: p[0], whisper: p.count > 1 ? p[1] : "")) }
