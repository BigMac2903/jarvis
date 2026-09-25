CREATE TABLE ai_usage (
    id text PRIMARY KEY,
    owner text NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    model text NOT NULL,
    task_type text NOT NULL,
    task_id text,
    agent text NOT NULL DEFAULT 'jarvis',
    connector text NOT NULL DEFAULT 'ai',
    input_tokens bigint NOT NULL DEFAULT 0,
    cached_tokens bigint NOT NULL DEFAULT 0,
    output_tokens bigint NOT NULL DEFAULT 0,
    reasoning_tokens bigint NOT NULL DEFAULT 0,
    estimated_cost numeric(18,8) NOT NULL DEFAULT 0,
    price_known boolean NOT NULL DEFAULT false,
    duration_ms bigint NOT NULL DEFAULT 0,
    status text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ai_usage_owner_date ON ai_usage(owner,created_at);
