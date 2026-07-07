namespace NeisAutoFill.Core.Scale;

/// <summary>평가척도 프리셋 저장/로드/활성 선택. 구현은 scales.json 파일 기반.</summary>
public interface IScaleStore
{
    /// <summary>내장 + 사용자 정의 프리셋 전체.</summary>
    IReadOnlyList<GradeScale> Presets { get; }

    /// <summary>현재 활성 척도. 매칭·생성이 이 척도를 공유한다.</summary>
    GradeScale Active { get; set; }

    /// <summary>사용자 정의 척도 추가 또는 동일 이름 갱신.</summary>
    void Upsert(GradeScale scale);

    /// <summary>이름으로 삭제 (내장 프리셋은 삭제 불가).</summary>
    bool Remove(string name);

    /// <summary>현재 상태를 영속화.</summary>
    void Save();
}
