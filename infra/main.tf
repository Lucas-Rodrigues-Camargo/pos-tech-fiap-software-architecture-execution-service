terraform {
  required_version = ">= 1.11"
  backend "s3" {}
  required_providers {
    aws = { source = "hashicorp/aws", version = "~> 6.0" }
  }
}
provider "aws" { region = var.region }
variable "region" { default = "us-east-1" }
variable "cluster_name" { type = string }
variable "namespace" { default = "oficina-phase4" }
data "aws_caller_identity" "current" {}
resource "aws_sqs_queue" "dlq" {
  name                      = "oficina-execution-dlq"
  sqs_managed_sse_enabled   = true
  message_retention_seconds = 1209600
}
resource "aws_sqs_queue" "input" {
  name                       = "oficina-execution"
  sqs_managed_sse_enabled    = true
  visibility_timeout_seconds = 120
  redrive_policy             = jsonencode({ deadLetterTargetArn = aws_sqs_queue.dlq.arn, maxReceiveCount = 5 })
}
resource "aws_ecr_repository" "app" {
  name                 = "oficina-execution"
  image_tag_mutability = "IMMUTABLE"
  image_scanning_configuration { scan_on_push = true }
}
resource "aws_iam_role" "app" {
  name = "oficina-phase4-execution"
  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow", Action = ["sts:AssumeRole", "sts:TagSession"]
      Principal = { Service = "pods.eks.amazonaws.com" }
    }]
  })
}
resource "aws_eks_pod_identity_association" "app" {
  cluster_name    = var.cluster_name
  namespace       = var.namespace
  service_account = "oficina-execution"
  role_arn        = aws_iam_role.app.arn
}
resource "aws_iam_role_policy" "app" {
  role = aws_iam_role.app.id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      { Effect = "Allow", Action = ["sqs:ReceiveMessage", "sqs:DeleteMessage", "sqs:GetQueueAttributes"], Resource = aws_sqs_queue.input.arn },
      { Effect = "Allow", Action = ["sqs:SendMessage"], Resource = [
        "arn:aws:sqs:${var.region}:${data.aws_caller_identity.current.account_id}:oficina-os", "arn:aws:sqs:${var.region}:${data.aws_caller_identity.current.account_id}:oficina-billing",
        "arn:aws:sqs:${var.region}:${data.aws_caller_identity.current.account_id}:oficina-execution",
      ] }
      , { Effect = "Allow", Action = ["dynamodb:GetItem", "dynamodb:PutItem", "dynamodb:Scan"], Resource = aws_dynamodb_table.jobs.arn }
    ]
  })
}
resource "aws_dynamodb_table" "jobs" {
  name         = "oficina-execution"
  billing_mode = "PAY_PER_REQUEST"
  hash_key     = "id"
  attribute {
    name = "id"
    type = "S"
  }
  point_in_time_recovery { enabled = true }
  server_side_encryption { enabled = true }
  deletion_protection_enabled = true
}
output "table_name" { value = aws_dynamodb_table.jobs.name }
output "queue_url" { value = aws_sqs_queue.input.url }
output "ecr_url" { value = aws_ecr_repository.app.repository_url }
