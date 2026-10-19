# Локальный контракт IPC Порог

Транспорт: named pipe `Porog.v1`, байтовый режим, JSON UTF-8, один запрос и один ответ на соединение, окончание `\n`. Размер запроса — до 262144 символов, таймаут клиента и соединения службы — 2 секунды. ACL разрешает SID владельца и SYSTEM; служба сверяет реальный SID через impersonation. SID из JSON не принимается.

Общий запрос:

```json
{"type":"GetStatus","requestId":"8b6d052a-0540-4689-ad0a-05f4994e6d7c","payload":{}}
```

Успех: `{"requestId":"...","ok":true,"payload":{...},"error":null}`.

| Команда | Payload | Ответ payload |
|---|---|---|
| `GetStatus` | `{}` | serviceState, now, activeSession, breakSession, lastCompleted, nextSchedule, warningKeys, issues, capabilities, configVersion |
| `GetConfig` | `{}` | AppConfig целиком |
| `SaveConfig` | expectedVersion, schedules[], settings | сохранённый AppConfig с новой configVersion |
| `StartBreak` | durationMinutes целое 3..12 | Session с абсолютным endsAt |
| `GetSession` | sessionId UUID | текущая или последняя завершённая Session |
| `GetDiagnostics` | `{}` | serviceVersion, configVersion, dataDirectory, capabilities, issues |

## Сохранение расписания

```json
{
  "type": "SaveConfig",
  "requestId": "b3202b72-c174-4fd6-bcc1-d05b22399f0e",
  "payload": {
    "expectedVersion": 0,
    "schedules": [{
      "id": "077a9892-b59e-47e0-995e-ccfa25d874c0",
      "name": "Диплом",
      "enabled": true,
      "startAt": "2026-10-20T19:00:00+07:00",
      "endAt": "2026-10-20T21:00:00+07:00",
      "domainTargets": ["youtube.com", "www.youtube.com"],
      "processTargets": ["Telegram.exe"],
      "intention": "Завершить раздел работы"
    }],
    "settings": {"warningSeconds": 60, "breakDurationMinutes": 5}
  }
}
```

ТЗ дополнено `expectedVersion` для защиты от потери правок. Даты сохраняются со смещением UTC. Служба заново валидирует данные и сравнивает начатые записи с сохранёнными. `SaveConfig` не может выключить активный блок.

## Ошибки

```json
{
  "requestId": "bd3ff65b-9aec-4a99-a858-afaa8fb33269",
  "ok": false,
  "payload": null,
  "error": {
    "code": "VALIDATION_ERROR",
    "message": "Длительность «Отхода» должна быть от 3 до 12 минут.",
    "details": [{"code":"VALIDATION_ERROR","message":"Длительность «Отхода» должна быть от 3 до 12 минут.","field":"durationMinutes"}],
    "retryable": false
  }
}
```

Коды: `VALIDATION_ERROR`, `SERVICE_UNAVAILABLE` (клиент), `PERMISSION_DENIED`, `HOSTS_WRITE_FAILED`, `PROCESS_CONTROL_FAILED`, `CONFIG_CORRUPTED`, `SCHEDULE_CONFLICT`, `ACTIVE_SESSION_LOCKED`, `CONFIG_VERSION_CONFLICT`, `SESSION_NOT_FOUND`, `INTERNAL_ERROR`.

Неисправный JSON даёт ошибку с пустым UUID, если исходный ID невозможно разобрать. Неавторизованное, чрезмерное или просроченное соединение закрывается без выполнения команды.

## Повторы

Клиент не повторяет изменение автоматически. При потере ответа `SaveConfig` прочитайте `GetConfig`, сверив содержимое и версию. В работающем экземпляре службы одинаковые UUID и тело идемпотентны; кэш ограничен 256 успешными изменениями. `StartBreak` сохраняет UUID как sessionId и узнаёт его после рестарта, пока сессия активна. Другой параллельный «Отход» отклоняется.

## Отличия от предложенного примера ТЗ

Домен и процесс представлены строками, как в JSON-примере ТЗ; флаг включения задаётся на уровне промежутка. `Session` использует присутствие в `activeSession`/`lastCompleted` вместо отдельного поля `state`. Ошибки содержат `code/message/field` вместо `field/reason`. Эти решения отражены в исходниках и здесь; внешних интеграций до MVP нет.
