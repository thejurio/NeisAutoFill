using System.Text;

namespace NeisAutoFill.Core;

/// <summary>나이스 글자수(바이트) 제한 사전 검사용 길이 계산.</summary>
public static class TextMetrics
{
    /// <summary>UTF-8 바이트 수 (나이스 4세대 바이트 제한 기준, 한글 3바이트).</summary>
    public static int Utf8Bytes(string text) => Encoding.UTF8.GetByteCount(text);

    /// <summary>"1234자 / 3702바이트" 형식 요약.</summary>
    public static string Summary(string text) =>
        $"{text.Length}자 / {Utf8Bytes(text)}바이트";
}
